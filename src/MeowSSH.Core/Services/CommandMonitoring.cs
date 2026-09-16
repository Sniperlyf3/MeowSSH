using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

public sealed record CommandMonitor(
    Guid Id,
    string Name,
    Guid HostId,
    string Command,
    int IntervalSeconds = 300,
    int TimeoutSeconds = 30,
    bool Enabled = true,
    bool NotifyOnChange = true,
    bool NotifyOnFailure = true,
    bool NotifyOnRecovery = true);

public enum CommandMonitorRunState
{
    Healthy,
    Failed,
    ConnectionFailed,
}

public enum CommandMonitorAlertKind
{
    OutputChanged,
    Failed,
    Recovered,
}

public sealed record CommandMonitorRun(
    Guid Id,
    Guid MonitorId,
    DateTimeOffset CheckedAtUtc,
    CommandMonitorRunState State,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? Message,
    bool OutputChanged,
    CommandMonitorAlertKind? AlertKind);

public sealed record CommandMonitorStatus(
    Guid MonitorId,
    DateTimeOffset? LastCheckedAtUtc,
    string? LastOutputHash,
    bool WasFailing,
    int? LastExitCode,
    string? LastMessage);

public sealed record CommandMonitorAlert(
    Guid MonitorId,
    string MonitorName,
    string HostLabel,
    CommandMonitorAlertKind Kind,
    string Title,
    string Message);

public interface ICommandMonitorAlertSink
{
    Task NotifyAsync(CommandMonitorAlert alert, CancellationToken cancellationToken = default);
}

public sealed class NoOpCommandMonitorAlertSink : ICommandMonitorAlertSink
{
    public Task NotifyAsync(CommandMonitorAlert alert, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public interface ICommandMonitorStore
{
    Task<IReadOnlyList<CommandMonitor>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CommandMonitor monitor, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CommandMonitorStatus?> GetStatusAsync(Guid monitorId, CancellationToken cancellationToken = default);
    Task SaveStatusAsync(CommandMonitorStatus status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandMonitorRun>> GetHistoryAsync(Guid monitorId, CancellationToken cancellationToken = default);
    Task AppendRunAsync(CommandMonitorRun run, CancellationToken cancellationToken = default);
}

public interface ICommandMonitoringService : IAsyncDisposable
{
    event EventHandler? Changed;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandMonitor>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CommandMonitor monitor, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);
    Task<CommandMonitorStatus?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandMonitorRun>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CommandMonitorRun> RunNowAsync(Guid id, CancellationToken cancellationToken = default);
    Task<int> RunDueAsync(CancellationToken cancellationToken = default);
}

public sealed class FileCommandMonitorStore(string path) : ICommandMonitorStore
{
    private const int MaxHistoryPerMonitor = 50;
    private readonly object _gate = new();
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A command-monitor file path is required.", nameof(path))
        : path;

    public Task<IReadOnlyList<CommandMonitor>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<CommandMonitor> result = [.. ReadUnsafe().Monitors
                .Select(Normalize)
                .OrderBy(static monitor => monitor.Name, StringComparer.OrdinalIgnoreCase)];
            return Task.FromResult(result);
        }
    }

    public Task SaveAsync(CommandMonitor monitor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeAndValidate(monitor);
        lock (_gate)
        {
            var document = ReadUnsafe();
            var monitors = document.Monitors.ToList();
            var index = monitors.FindIndex(item => item.Id == normalized.Id);
            if (index >= 0) monitors[index] = normalized;
            else monitors.Add(normalized);
            WriteUnsafe(document with { Monitors = monitors });
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var document = ReadUnsafe();
            WriteUnsafe(document with
            {
                Monitors = document.Monitors.Where(item => item.Id != id).ToArray(),
                Statuses = document.Statuses.Where(item => item.MonitorId != id).ToArray(),
                Runs = document.Runs.Where(item => item.MonitorId != id).ToArray(),
            });
        }
        return Task.CompletedTask;
    }

    public Task<CommandMonitorStatus?> GetStatusAsync(Guid monitorId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(ReadUnsafe().Statuses.FirstOrDefault(status => status.MonitorId == monitorId));
    }

    public Task SaveStatusAsync(CommandMonitorStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var document = ReadUnsafe();
            var statuses = document.Statuses.ToList();
            var index = statuses.FindIndex(item => item.MonitorId == status.MonitorId);
            if (index >= 0) statuses[index] = status;
            else statuses.Add(status);
            WriteUnsafe(document with { Statuses = statuses });
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CommandMonitorRun>> GetHistoryAsync(Guid monitorId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<CommandMonitorRun> result = [.. ReadUnsafe().Runs
                .Where(run => run.MonitorId == monitorId)
                .OrderByDescending(static run => run.CheckedAtUtc)];
            return Task.FromResult(result);
        }
    }

    public Task AppendRunAsync(CommandMonitorRun run, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var document = ReadUnsafe();
            var runs = document.Runs.Append(run)
                .GroupBy(static item => item.MonitorId)
                .SelectMany(group => group
                    .OrderByDescending(static item => item.CheckedAtUtc)
                    .Take(MaxHistoryPerMonitor))
                .ToArray();
            WriteUnsafe(document with { Runs = runs });
        }
        return Task.CompletedTask;
    }

    internal static CommandMonitor NormalizeAndValidate(CommandMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var normalized = Normalize(monitor);
        if (normalized.Id == Guid.Empty) throw new ArgumentException("A monitor ID is required.", nameof(monitor));
        if (string.IsNullOrWhiteSpace(normalized.Name)) throw new ArgumentException("A monitor name is required.", nameof(monitor));
        if (normalized.Name.Length > 80) throw new ArgumentException("Monitor names must be 80 characters or fewer.", nameof(monitor));
        if (normalized.HostId == Guid.Empty) throw new ArgumentException("A host is required.", nameof(monitor));
        if (string.IsNullOrWhiteSpace(normalized.Command)) throw new ArgumentException("A command is required.", nameof(monitor));
        if (normalized.Command.Length > 16_384) throw new ArgumentException("Commands must be 16 KiB or smaller.", nameof(monitor));
        if (normalized.IntervalSeconds is < 60 or > 86_400) throw new ArgumentException("Monitor interval must be between 60 seconds and 24 hours.", nameof(monitor));
        if (normalized.TimeoutSeconds is < 1 or > 600) throw new ArgumentException("Command timeout must be between 1 and 600 seconds.", nameof(monitor));
        return normalized;
    }

    private static CommandMonitor Normalize(CommandMonitor monitor) => monitor with
    {
        Name = monitor.Name.Trim(),
        Command = monitor.Command.Trim(),
    };

    private CommandMonitorDocument ReadUnsafe()
    {
        if (!File.Exists(_path)) return CommandMonitorDocument.Empty;
        try
        {
            return JsonSerializer.Deserialize(
                       File.ReadAllText(_path),
                       CommandMonitorJsonContext.Default.CommandMonitorDocument)
                   ?? CommandMonitorDocument.Empty;
        }
        catch (JsonException)
        {
            return CommandMonitorDocument.Empty;
        }
    }

    private void WriteUnsafe(CommandMonitorDocument document)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(document, CommandMonitorJsonContext.Default.CommandMonitorDocument);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed class MemoryCommandMonitorStore : ICommandMonitorStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CommandMonitor> _monitors = [];
    private readonly Dictionary<Guid, CommandMonitorStatus> _statuses = [];
    private readonly Dictionary<Guid, List<CommandMonitorRun>> _runs = [];

    public Task<IReadOnlyList<CommandMonitor>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<CommandMonitor> result = [.. _monitors.Values.OrderBy(static monitor => monitor.Name, StringComparer.OrdinalIgnoreCase)];
            return Task.FromResult(result);
        }
    }

    public Task SaveAsync(CommandMonitor monitor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = FileCommandMonitorStore.NormalizeAndValidate(monitor);
        lock (_gate) _monitors[normalized.Id] = normalized;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _monitors.Remove(id);
            _statuses.Remove(id);
            _runs.Remove(id);
        }
        return Task.CompletedTask;
    }

    public Task<CommandMonitorStatus?> GetStatusAsync(Guid monitorId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_statuses.GetValueOrDefault(monitorId));
    }

    public Task SaveStatusAsync(CommandMonitorStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _statuses[status.MonitorId] = status;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CommandMonitorRun>> GetHistoryAsync(Guid monitorId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<CommandMonitorRun> result = _runs.TryGetValue(monitorId, out var runs)
                ? [.. runs.OrderByDescending(static run => run.CheckedAtUtc)]
                : [];
            return Task.FromResult(result);
        }
    }

    public Task AppendRunAsync(CommandMonitorRun run, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_runs.TryGetValue(run.MonitorId, out var runs)) _runs[run.MonitorId] = runs = [];
            runs.Insert(0, run);
            if (runs.Count > 50) runs.RemoveRange(50, runs.Count - 50);
        }
        return Task.CompletedTask;
    }
}

public sealed class CommandMonitoringService(
    ICommandMonitorStore store,
    IHostDirectory hostDirectory,
    ICredentialResolver credentials,
    ISshEngine engine,
    ISshPrompts prompts,
    IEntitlementService entitlements,
    ICommandMonitorAlertSink alerts,
    TimeProvider? timeProvider = null) : ICommandMonitoringService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _runGate = new(4, 4);
    private readonly object _schedulerGate = new();
    private CancellationTokenSource? _schedulerCancellation;
    private Task? _schedulerTask;

    public event EventHandler? Changed;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_schedulerGate)
        {
            if (_schedulerTask is { IsCompleted: false }) return Task.CompletedTask;
            _schedulerCancellation?.Dispose();
            _schedulerCancellation = new CancellationTokenSource();
            _schedulerTask = SchedulerLoopAsync(_schedulerCancellation.Token);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CommandMonitor>> GetAllAsync(CancellationToken cancellationToken = default) =>
        store.GetAllAsync(cancellationToken);

    public async Task SaveAsync(CommandMonitor monitor, CancellationToken cancellationToken = default)
    {
        RequirePro();
        await store.SaveAsync(monitor, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Deleting user data is never entitlement-gated.
        await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        if (enabled) RequirePro();
        var monitor = (await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("The command monitor no longer exists.");
        await store.SaveAsync(monitor with { Enabled = enabled }, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<CommandMonitorStatus?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.GetStatusAsync(id, cancellationToken);

    public Task<IReadOnlyList<CommandMonitorRun>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.GetHistoryAsync(id, cancellationToken);

    public async Task<CommandMonitorRun> RunNowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequirePro();
        var monitor = (await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("The command monitor no longer exists.");
        return await RunMonitorAsync(monitor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RunDueAsync(CancellationToken cancellationToken = default)
    {
        if (!entitlements.Has(PremiumFeature.CommandMonitoring)) return 0;

        var now = _timeProvider.GetUtcNow();
        var monitors = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var due = new List<CommandMonitor>();
        foreach (var monitor in monitors.Where(static monitor => monitor.Enabled))
        {
            var status = await store.GetStatusAsync(monitor.Id, cancellationToken).ConfigureAwait(false);
            if (status?.LastCheckedAtUtc is null
                || status.LastCheckedAtUtc.Value.AddSeconds(monitor.IntervalSeconds) <= now)
                due.Add(monitor);
        }

        await Task.WhenAll(due.Select(monitor => RunMonitorAsync(monitor, cancellationToken))).ConfigureAwait(false);
        return due.Count;
    }

    private async Task<CommandMonitorRun> RunMonitorAsync(CommandMonitor monitor, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var checkedAt = _timeProvider.GetUtcNow();
            var previous = await store.GetStatusAsync(monitor.Id, cancellationToken).ConfigureAwait(false);
            var hosts = await hostDirectory.GetHostsAsync(cancellationToken).ConfigureAwait(false);
            var host = hosts.Select(static item => item.Host).FirstOrDefault(item => item.Id == monitor.HostId);

            CommandMonitorRunState state;
            int? exitCode = null;
            string stdout = string.Empty;
            string stderr = string.Empty;
            string? message = null;
            var hostLabel = host?.Label ?? "Deleted host";

            if (host is null || host.IsDeleted)
            {
                state = CommandMonitorRunState.ConnectionFailed;
                message = "The saved host no longer exists.";
            }
            else if (host.Protocol != HostProtocol.Ssh)
            {
                state = CommandMonitorRunState.ConnectionFailed;
                message = "Command Monitoring currently supports SSH hosts only.";
            }
            else
            {
                ISshConnection? connection = null;
                try
                {
                    using var resolved = await credentials.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
                    var target = !string.IsNullOrWhiteSpace(resolved.Username)
                        ? host with { Username = resolved.Username }
                        : host;
                    connection = await engine.ConnectAsync(target, resolved, prompts, cancellationToken).ConfigureAwait(false);
                    var result = await connection.RunCommandAsync(
                        monitor.Command,
                        TimeSpan.FromSeconds(monitor.TimeoutSeconds),
                        cancellationToken).ConfigureAwait(false);
                    exitCode = result.ExitCode;
                    stdout = Limit(result.StandardOutput);
                    stderr = Limit(result.StandardError);
                    state = result.Succeeded ? CommandMonitorRunState.Healthy : CommandMonitorRunState.Failed;
                    if (!result.Succeeded) message = $"Command exited with status {result.ExitCode}.";
                }
                catch (SshException exception)
                {
                    state = CommandMonitorRunState.ConnectionFailed;
                    message = exception.Message;
                }
                finally
                {
                    if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                }
            }

            var failing = state != CommandMonitorRunState.Healthy;
            var outputHash = OutputHash(stdout, stderr);
            var outputChanged = previous?.LastOutputHash is { Length: > 0 } oldHash
                && !string.Equals(oldHash, outputHash, StringComparison.Ordinal);

            CommandMonitorAlertKind? alertKind = null;
            if (failing && monitor.NotifyOnFailure && previous?.WasFailing != true)
                alertKind = CommandMonitorAlertKind.Failed;
            else if (!failing && previous?.WasFailing == true && monitor.NotifyOnRecovery)
                alertKind = CommandMonitorAlertKind.Recovered;
            else if (!failing && outputChanged && monitor.NotifyOnChange)
                alertKind = CommandMonitorAlertKind.OutputChanged;

            var run = new CommandMonitorRun(
                Guid.NewGuid(), monitor.Id, checkedAt, state, exitCode,
                stdout, stderr, message, outputChanged, alertKind);

            await store.AppendRunAsync(run, cancellationToken).ConfigureAwait(false);
            await store.SaveStatusAsync(new CommandMonitorStatus(
                monitor.Id,
                checkedAt,
                outputHash,
                failing,
                exitCode,
                message), cancellationToken).ConfigureAwait(false);

            if (alertKind is { } kind)
            {
                var alert = BuildAlert(monitor, hostLabel, kind, run);
                await alerts.NotifyAsync(alert, cancellationToken).ConfigureAwait(false);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return run;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _timeProvider);
        try
        {
            await RunDueSafelyAsync(cancellationToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await RunDueSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunDueSafelyAsync(CancellationToken cancellationToken)
    {
        try { await RunDueAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or SshException or InvalidOperationException)
        {
            // Individual executions persist their own connection failures. A storage/runtime
            // failure must not kill the scheduler permanently; the next tick retries.
        }
    }

    private void RequirePro()
    {
        if (!entitlements.Has(PremiumFeature.CommandMonitoring))
            throw new InvalidOperationException("Command Monitoring requires MeowSSH Pro.");
    }

    private static CommandMonitorAlert BuildAlert(
        CommandMonitor monitor,
        string hostLabel,
        CommandMonitorAlertKind kind,
        CommandMonitorRun run) => kind switch
    {
        CommandMonitorAlertKind.Failed => new(
            monitor.Id, monitor.Name, hostLabel, kind,
            $"{monitor.Name} failed",
            run.Message ?? $"The check on {hostLabel} failed."),
        CommandMonitorAlertKind.Recovered => new(
            monitor.Id, monitor.Name, hostLabel, kind,
            $"{monitor.Name} recovered",
            $"The check on {hostLabel} is healthy again."),
        _ => new(
            monitor.Id, monitor.Name, hostLabel, kind,
            $"{monitor.Name} changed",
            $"Command output changed on {hostLabel}."),
    };

    private static string OutputHash(string stdout, string stderr)
    {
        var bytes = Encoding.UTF8.GetBytes(stdout + "\n\0stderr\0\n" + stderr);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Limit(string value)
    {
        const int limit = 32 * 1024;
        if (value.Length <= limit) return value;
        return value[..limit] + "\n… output truncated by MeowSSH …";
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        Task? scheduler;
        lock (_schedulerGate)
        {
            cancellation = _schedulerCancellation;
            scheduler = _schedulerTask;
            _schedulerCancellation = null;
            _schedulerTask = null;
        }

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (scheduler is not null)
            {
                try { await scheduler.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            cancellation.Dispose();
        }
        _runGate.Dispose();
    }
}

public sealed record CommandMonitorDocument(
    IReadOnlyList<CommandMonitor> Monitors,
    IReadOnlyList<CommandMonitorStatus> Statuses,
    IReadOnlyList<CommandMonitorRun> Runs)
{
    public static CommandMonitorDocument Empty { get; } = new([], [], []);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(CommandMonitorDocument))]
internal sealed partial class CommandMonitorJsonContext : JsonSerializerContext
{
}
