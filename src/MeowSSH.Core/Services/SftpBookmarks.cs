using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Services;

public sealed record SftpBookmark(
    Guid Id,
    Guid HostId,
    string Label,
    string Path,
    DateTimeOffset CreatedAtUtc);

public interface ISftpBookmarkStore
{
    Task<IReadOnlyList<SftpBookmark>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SftpBookmark bookmark, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISftpBookmarkService
{
    Task<IReadOnlyList<SftpBookmark>> GetForHostAsync(Guid hostId, CancellationToken cancellationToken = default);
    Task<SftpBookmark> SaveAsync(Guid hostId, string label, string path, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class SftpBookmarkService(
    ISftpBookmarkStore store,
    IEntitlementService entitlements,
    TimeProvider? timeProvider = null) : ISftpBookmarkService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<SftpBookmark>> GetForHostAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        EnsureEntitled();
        if (hostId == Guid.Empty) throw new ArgumentException("A host ID is required.", nameof(hostId));
        return [.. (await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(bookmark => bookmark.HostId == hostId)
            .OrderBy(bookmark => bookmark.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(bookmark => bookmark.Path, StringComparer.Ordinal)];
    }

    public async Task<SftpBookmark> SaveAsync(
        Guid hostId,
        string label,
        string path,
        CancellationToken cancellationToken = default)
    {
        EnsureEntitled();
        if (hostId == Guid.Empty) throw new ArgumentException("A host ID is required.", nameof(hostId));
        var normalizedLabel = NormalizeLabel(label);
        var normalizedPath = NormalizePath(path);
        var all = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var existing = all.FirstOrDefault(bookmark =>
            bookmark.HostId == hostId && string.Equals(bookmark.Path, normalizedPath, StringComparison.Ordinal));

        var bookmark = existing is null
            ? new SftpBookmark(Guid.NewGuid(), hostId, normalizedLabel, normalizedPath, _timeProvider.GetUtcNow())
            : existing with { Label = normalizedLabel };
        await store.SaveAsync(bookmark, cancellationToken).ConfigureAwait(false);
        return bookmark;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureEntitled();
        if (id == Guid.Empty) throw new ArgumentException("A bookmark ID is required.", nameof(id));
        return store.DeleteAsync(id, cancellationToken);
    }

    private void EnsureEntitled()
    {
        if (!entitlements.Has(PremiumFeature.AdvancedSftp))
            throw new InvalidOperationException("SFTP remote bookmarks require MeowSSH Pro.");
    }

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/', StringComparison.Ordinal))
            throw new ArgumentException("An absolute remote path is required.", nameof(path));
        if (trimmed.Length > 4096)
            throw new ArgumentException("Remote bookmark paths must be 4096 characters or fewer.", nameof(path));
        while (trimmed.Length > 1 && trimmed.EndsWith('/', StringComparison.Ordinal))
            trimmed = trimmed[..^1];
        return trimmed;
    }

    private static string NormalizeLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var trimmed = label.Trim();
        if (trimmed.Length > 80)
            throw new ArgumentException("Bookmark labels must be 80 characters or fewer.", nameof(label));
        return trimmed;
    }
}

/// <summary>App-private persistence containing only host IDs and remote paths, never credentials.</summary>
public sealed class FileSftpBookmarkStore(string path) : ISftpBookmarkStore
{
    private readonly object _gate = new();
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A bookmark file path is required.", nameof(path))
        : path;

    public Task<IReadOnlyList<SftpBookmark>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<SftpBookmark> result = ReadUnsafe();
            return Task.FromResult(result);
        }
    }

    public Task SaveAsync(SftpBookmark bookmark, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var items = ReadUnsafe().ToList();
            var index = items.FindIndex(item => item.Id == bookmark.Id);
            if (index >= 0) items[index] = bookmark;
            else items.Add(bookmark);
            WriteUnsafe(items);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var items = ReadUnsafe().ToList();
            if (items.RemoveAll(item => item.Id == id) > 0) WriteUnsafe(items);
        }
        return Task.CompletedTask;
    }

    private IReadOnlyList<SftpBookmark> ReadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, SftpBookmarkJsonContext.Default.SftpBookmarkDocument)?.Bookmarks ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteUnsafe(IReadOnlyList<SftpBookmark> bookmarks)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var document = new SftpBookmarkDocument([
            .. bookmarks.OrderBy(bookmark => bookmark.HostId).ThenBy(bookmark => bookmark.Label, StringComparer.OrdinalIgnoreCase),
        ]);
        var json = JsonSerializer.Serialize(document, SftpBookmarkJsonContext.Default.SftpBookmarkDocument);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed class MemorySftpBookmarkStore : ISftpBookmarkStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SftpBookmark> _items = [];

    public Task<IReadOnlyList<SftpBookmark>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<SftpBookmark>>([.. _items.Values]);
    }

    public Task SaveAsync(SftpBookmark bookmark, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _items[bookmark.Id] = bookmark;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _items.Remove(id);
        return Task.CompletedTask;
    }
}

public sealed record SftpBookmarkDocument(IReadOnlyList<SftpBookmark> Bookmarks);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(SftpBookmarkDocument))]
internal sealed partial class SftpBookmarkJsonContext : JsonSerializerContext
{
}
