using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowSSH.Core.Services;

/// <summary>
/// Local, append-only JSON record of finished transfer-queue attempts. No entitlement check
/// lives here -- like <see cref="Services.FileSessionLogService"/>, what already happened stays
/// the user's own record to read, search and delete regardless of the current plan. Gating who
/// may add a *new* attempt is <see cref="TransferQueueService"/>'s job, not the store's.
/// </summary>
public sealed class FileTransferHistoryService(string path) : ITransferHistoryService
{
    private const int MaxEntries = 500;
    private readonly object _gate = new();
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A transfer-history file path is required.", nameof(path))
        : path;

    public event EventHandler? Changed;

    public Task<IReadOnlyList<TransferHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<TransferHistoryEntry> entries = [.. ReadUnsafe().OrderByDescending(static entry => entry.EndedAtUtc)];
            return Task.FromResult(entries);
        }
    }

    public Task RecordAsync(TransferHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var entries = ReadUnsafe();
            entries.Add(entry);
            // Oldest-first trim keeps the file bounded without ever losing the most recent
            // attempts, which are what a user checking "did that just work" actually wants.
            if (entries.Count > MaxEntries)
                entries = [.. entries.OrderByDescending(static item => item.EndedAtUtc).Take(MaxEntries)];
            WriteUnsafe(entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == Guid.Empty) throw new ArgumentException("A transfer-history ID is required.", nameof(id));

        lock (_gate)
        {
            var entries = ReadUnsafe();
            if (entries.RemoveAll(entry => entry.Id == id) == 0) return Task.CompletedTask;
            WriteUnsafe(entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) WriteUnsafe([]);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private List<TransferHistoryEntry> ReadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, TransferHistoryJsonContext.Default.TransferHistoryDocument)?.Entries
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            // Preserve a corrupt file for recovery/support instead of destroying it.
            return [];
        }
    }

    private void WriteUnsafe(List<TransferHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var document = new TransferHistoryDocument(entries);
        var json = JsonSerializer.Serialize(document, TransferHistoryJsonContext.Default.TransferHistoryDocument);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed record TransferHistoryDocument(IReadOnlyList<TransferHistoryEntry> Entries);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(TransferHistoryDocument))]
internal sealed partial class TransferHistoryJsonContext : JsonSerializerContext
{
}
