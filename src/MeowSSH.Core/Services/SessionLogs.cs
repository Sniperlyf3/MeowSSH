using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;

namespace MeowSSH.Core.Services;

public sealed record SessionLogSummary(
    Guid Id,
    Guid HostId,
    string HostLabel,
    HostProtocol Protocol,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    int? ExitCode,
    string? EndReason,
    long BytesCaptured,
    bool Truncated)
{
    public TimeSpan? Duration => EndedAtUtc is { } ended ? ended - StartedAtUtc : null;
}

public interface ISessionLogCapture : IAsyncDisposable
{
    Guid Id { get; }

    /// <summary>
    /// Queues terminal output for persistence without blocking the terminal's output pump.
    /// Returns false when the bounded writer is saturated or the per-log size cap was reached.
    /// </summary>
    bool TryAppend(ReadOnlyMemory<byte> output);

    Task CompleteAsync(
        int? exitCode = null,
        string? endReason = null,
        CancellationToken cancellationToken = default);
}

public interface ISessionLogService
{
    bool AutoRecord { get; }
    event EventHandler? Changed;

    /// <summary>Enabling recording requires Pro. Disabling it is always allowed.</summary>
    Task SetAutoRecordAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Starts a new output-only transcript. Requires the SessionLogs entitlement.</summary>
    Task<ISessionLogCapture> StartAsync(HostRecord host, CancellationToken cancellationToken = default);

    /// <summary>
    /// Existing logs remain readable/exportable/deletable after entitlement expiry so user data
    /// is never held hostage by a subscription or licence state change.
    /// </summary>
    Task<IReadOnlyList<SessionLogSummary>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionLogSummary>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<string> ReadAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Local, output-only terminal transcript storage. Typed input is deliberately never recorded,
/// avoiding accidental persistence of passwords/passphrases entered into interactive prompts.
/// </summary>
public sealed class FileSessionLogService(
    string directory,
    IEntitlementService entitlements,
    TimeProvider? timeProvider = null) : ISessionLogService
{
    private const int MaxLogs = 100;
    private const long MaxTotalBytes = 25L * 1024 * 1024;
    private const long MaxLogBytes = 2L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _directory = string.IsNullOrWhiteSpace(directory)
        ? throw new ArgumentException("A session-log directory is required.", nameof(directory))
        : directory;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool AutoRecord
    {
        get
        {
            lock (_gate) return ReadDocumentUnsafe().AutoRecord;
        }
    }

    public event EventHandler? Changed;

    public Task SetAutoRecordAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled && !entitlements.Has(PremiumFeature.SessionLogs))
            throw new InvalidOperationException("Session logging requires MeowSSH Pro.");

        lock (_gate)
        {
            var document = ReadDocumentUnsafe();
            if (document.AutoRecord == enabled) return Task.CompletedTask;
            WriteDocumentUnsafe(document with { AutoRecord = enabled });
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<ISessionLogCapture> StartAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        cancellationToken.ThrowIfCancellationRequested();
        if (!entitlements.Has(PremiumFeature.SessionLogs))
            throw new InvalidOperationException("Session logging requires MeowSSH Pro.");

        Directory.CreateDirectory(_directory);
        var summary = new SessionLogSummary(
            Guid.NewGuid(),
            host.Id,
            host.Label,
            host.Protocol,
            _timeProvider.GetUtcNow(),
            EndedAtUtc: null,
            ExitCode: null,
            EndReason: null,
            BytesCaptured: 0,
            Truncated: false);

        lock (_gate)
        {
            var document = ReadDocumentUnsafe();
            var logs = document.Logs.Where(static log => log.Id != Guid.Empty).ToList();
            logs.Add(summary);
            WriteDocumentUnsafe(document with { Logs = logs });
        }
        Changed?.Invoke(this, EventArgs.Empty);

        ISessionLogCapture capture = new Capture(this, summary, LogPath(summary.Id), MaxLogBytes);
        return Task.FromResult(capture);
    }

    public Task<IReadOnlyList<SessionLogSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<SessionLogSummary> result = [..
                ReadDocumentUnsafe().Logs
                    .OrderByDescending(static log => log.StartedAtUtc)
            ];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<SessionLogSummary>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var needle = query?.Trim() ?? string.Empty;
        if (needle.Length == 0) return GetAllAsync(cancellationToken);

        List<SessionLogSummary> logs;
        lock (_gate) logs = [.. ReadDocumentUnsafe().Logs.OrderByDescending(static log => log.StartedAtUtc)];

        var matches = new List<SessionLogSummary>();
        foreach (var log in logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (log.HostLabel.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (log.EndReason?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                matches.Add(log);
                continue;
            }

            var path = LogPath(log.Id);
            if (!File.Exists(path)) continue;
            var text = CleanTerminalText(File.ReadAllBytes(path));
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) matches.Add(log);
        }

        return Task.FromResult<IReadOnlyList<SessionLogSummary>>(matches);
    }

    public Task<string> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == Guid.Empty) throw new ArgumentException("A session-log ID is required.", nameof(id));

        lock (_gate)
        {
            if (!ReadDocumentUnsafe().Logs.Any(log => log.Id == id))
                throw new KeyNotFoundException("The session log no longer exists.");
        }

        var path = LogPath(id);
        if (!File.Exists(path)) return Task.FromResult(string.Empty);
        return Task.FromResult(CleanTerminalText(File.ReadAllBytes(path)));
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == Guid.Empty) throw new ArgumentException("A session-log ID is required.", nameof(id));

        lock (_gate)
        {
            var document = ReadDocumentUnsafe();
            var logs = document.Logs.Where(log => log.Id != id).ToList();
            if (logs.Count == document.Logs.Count) return Task.CompletedTask;
            WriteDocumentUnsafe(document with { Logs = logs });
            TryDelete(LogPath(id));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var document = ReadDocumentUnsafe();
            foreach (var log in document.Logs.Where(static log => log.EndedAtUtc is not null))
                TryDelete(LogPath(log.Id));
            var active = document.Logs.Where(static log => log.EndedAtUtc is null).ToArray();
            WriteDocumentUnsafe(document with { Logs = active });
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void FinalizeCapture(SessionLogSummary original, long bytesCaptured, bool truncated, int? exitCode, string? endReason)
    {
        lock (_gate)
        {
            var document = ReadDocumentUnsafe();
            var logs = document.Logs.ToList();
            var index = logs.FindIndex(log => log.Id == original.Id);
            if (index >= 0)
            {
                logs[index] = original with
                {
                    EndedAtUtc = _timeProvider.GetUtcNow(),
                    ExitCode = exitCode,
                    EndReason = string.IsNullOrWhiteSpace(endReason) ? null : endReason.Trim(),
                    BytesCaptured = bytesCaptured,
                    Truncated = truncated,
                };
            }

            logs = ApplyRetentionUnsafe(logs);
            WriteDocumentUnsafe(document with { Logs = logs });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<SessionLogSummary> ApplyRetentionUnsafe(List<SessionLogSummary> logs)
    {
        var completed = logs
            .Where(static log => log.EndedAtUtc is not null)
            .OrderByDescending(static log => log.StartedAtUtc)
            .ToList();
        var keep = new HashSet<Guid>(logs.Where(static log => log.EndedAtUtc is null).Select(static log => log.Id));
        long retainedBytes = 0;

        foreach (var log in completed)
        {
            var path = LogPath(log.Id);
            var length = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (keep.Count < MaxLogs && retainedBytes + length <= MaxTotalBytes)
            {
                keep.Add(log.Id);
                retainedBytes += length;
            }
            else
            {
                TryDelete(path);
            }
        }

        return logs.Where(log => keep.Contains(log.Id)).ToList();
    }

    private SessionLogDocument ReadDocumentUnsafe()
    {
        var path = IndexPath;
        if (!File.Exists(path)) return new SessionLogDocument(false, []);
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, SessionLogJsonContext.Default.SessionLogDocument)
                ?? new SessionLogDocument(false, []);
        }
        catch (JsonException)
        {
            return new SessionLogDocument(false, []);
        }
    }

    private void WriteDocumentUnsafe(SessionLogDocument document)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(document, SessionLogJsonContext.Default.SessionLogDocument);
        var temporary = IndexPath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, IndexPath, overwrite: true);
    }

    private string IndexPath => Path.Combine(_directory, "index.json");
    private string LogPath(Guid id) => Path.Combine(_directory, $"{id:N}.log");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string CleanTerminalText(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (current == '\u001b')
            {
                if (i + 1 >= text.Length) break;
                var next = text[++i];
                if (next == '[')
                {
                    while (i + 1 < text.Length)
                    {
                        var value = text[++i];
                        if (value is >= '@' and <= '~') break;
                    }
                }
                else if (next == ']')
                {
                    while (i + 1 < text.Length)
                    {
                        var value = text[++i];
                        if (value == '\a') break;
                        if (value == '\u001b' && i + 1 < text.Length && text[i + 1] == '\\')
                        {
                            i++;
                            break;
                        }
                    }
                }
                continue;
            }

            if (current is '\n' or '\r' or '\t' || current >= ' ')
                builder.Append(current);
        }
        return builder.ToString();
    }

    private sealed class Capture : ISessionLogCapture
    {
        private readonly FileSessionLogService _owner;
        private readonly SessionLogSummary _summary;
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly object _appendGate = new();
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        private readonly Task _writer;
        private long _acceptedBytes;
        private bool _truncated;
        private bool _completed;

        public Capture(FileSessionLogService owner, SessionLogSummary summary, string path, long maxBytes)
        {
            _owner = owner;
            _summary = summary;
            _path = path;
            _maxBytes = maxBytes;
            _writer = WriteAsync();
        }

        public Guid Id => _summary.Id;

        public bool TryAppend(ReadOnlyMemory<byte> output)
        {
            if (output.Length == 0) return true;

            lock (_appendGate)
            {
                if (_completed) return false;
                var remaining = _maxBytes - _acceptedBytes;
                if (remaining <= 0)
                {
                    _truncated = true;
                    return false;
                }

                var take = (int)Math.Min(remaining, output.Length);
                var chunk = output.Slice(0, take).ToArray();
                if (!_queue.Writer.TryWrite(chunk))
                {
                    _truncated = true;
                    return false;
                }

                _acceptedBytes += take;
                if (take < output.Length) _truncated = true;
                return take == output.Length;
            }
        }

        public async Task CompleteAsync(int? exitCode = null, string? endReason = null, CancellationToken cancellationToken = default)
        {
            lock (_appendGate)
            {
                if (_completed) return;
                _completed = true;
                _queue.Writer.TryComplete();
            }

            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            _owner.FinalizeCapture(_summary, _acceptedBytes, _truncated, exitCode, endReason);
        }

        public async ValueTask DisposeAsync()
        {
            await CompleteAsync(endReason: "Session ended.", cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        private async Task WriteAsync()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = new FileStream(
                _path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            await foreach (var chunk in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                await stream.WriteAsync(chunk).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }
}

public sealed record SessionLogDocument(bool AutoRecord, IReadOnlyList<SessionLogSummary> Logs);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(SessionLogDocument))]
internal sealed partial class SessionLogJsonContext : JsonSerializerContext
{
}
