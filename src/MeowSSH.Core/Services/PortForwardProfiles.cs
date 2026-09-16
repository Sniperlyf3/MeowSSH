using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

public sealed record PortForwardProfile(
    Guid Id,
    Guid HostId,
    string Label,
    SshForwardKind Kind,
    string ListenAddress,
    string? Destination,
    bool AllowNonLoopbackBind,
    bool RequireSocksAuth,
    int MaxConnections,
    bool UseUnixSocket,
    DateTimeOffset CreatedAtUtc);

public interface IPortForwardProfileStore
{
    Task<IReadOnlyList<PortForwardProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PortForwardProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IPortForwardProfileService
{
    Task<IReadOnlyList<PortForwardProfile>> GetForHostAsync(Guid hostId, CancellationToken cancellationToken = default);
    Task<PortForwardProfile> SaveAsync(PortForwardProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class PortForwardProfileService(
    IPortForwardProfileStore store,
    IEntitlementService entitlements,
    TimeProvider? timeProvider = null) : IPortForwardProfileService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<PortForwardProfile>> GetForHostAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        EnsureEntitled();
        if (hostId == Guid.Empty) throw new ArgumentException("A host ID is required.", nameof(hostId));
        return [.. (await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(profile => profile.HostId == hostId)
            .OrderBy(profile => profile.Label, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<PortForwardProfile> SaveAsync(PortForwardProfile profile, CancellationToken cancellationToken = default)
    {
        EnsureEntitled();
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);
        var normalized = profile with
        {
            Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id,
            Label = profile.Label.Trim(),
            ListenAddress = profile.ListenAddress.Trim(),
            Destination = string.IsNullOrWhiteSpace(profile.Destination) ? null : profile.Destination.Trim(),
            CreatedAtUtc = profile.CreatedAtUtc == default ? _timeProvider.GetUtcNow() : profile.CreatedAtUtc,
        };
        await store.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureEntitled();
        if (id == Guid.Empty) throw new ArgumentException("A profile ID is required.", nameof(id));
        return store.DeleteAsync(id, cancellationToken);
    }

    private void EnsureEntitled()
    {
        if (!entitlements.Has(PremiumFeature.PortForwardProfiles))
            throw new InvalidOperationException("Saved port forward profiles require MeowSSH Pro.");
    }

    private static void Validate(PortForwardProfile profile)
    {
        if (profile.HostId == Guid.Empty) throw new ArgumentException("A host ID is required.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Label) || profile.Label.Trim().Length > 80)
            throw new ArgumentException("Profile labels must be between 1 and 80 characters.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.ListenAddress) || profile.ListenAddress.Trim().Length > 4096)
            throw new ArgumentException("A valid listen address is required.", nameof(profile));
        if (profile.Kind != SshForwardKind.Socks && string.IsNullOrWhiteSpace(profile.Destination))
            throw new ArgumentException("A destination is required for local and remote forwards.", nameof(profile));
        if (profile.Destination?.Trim().Length > 4096)
            throw new ArgumentException("The destination is too long.", nameof(profile));
        if (profile.MaxConnections is < 0 or > 65535)
            throw new ArgumentException("Maximum connections must be between 0 and 65535.", nameof(profile));
        if (profile.UseUnixSocket && profile.Kind == SshForwardKind.Remote)
            throw new ArgumentException("Remote forwards cannot use a local Unix socket.", nameof(profile));
    }
}

/// <summary>App-private profile persistence. SOCKS passwords are deliberately never represented in this model.</summary>
public sealed class FilePortForwardProfileStore(string path) : IPortForwardProfileStore
{
    private readonly object _gate = new();
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A profile file path is required.", nameof(path))
        : path;

    public Task<IReadOnlyList<PortForwardProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<PortForwardProfile>>(ReadUnsafe());
    }

    public Task SaveAsync(PortForwardProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var items = ReadUnsafe().ToList();
            var index = items.FindIndex(item => item.Id == profile.Id);
            if (index >= 0) items[index] = profile;
            else items.Add(profile);
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

    private IReadOnlyList<PortForwardProfile> ReadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                PortForwardProfileJsonContext.Default.PortForwardProfileDocument)?.Profiles ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteUnsafe(IReadOnlyList<PortForwardProfile> profiles)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(
            new PortForwardProfileDocument([.. profiles.OrderBy(profile => profile.HostId).ThenBy(profile => profile.Label, StringComparer.OrdinalIgnoreCase)]),
            PortForwardProfileJsonContext.Default.PortForwardProfileDocument);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed class MemoryPortForwardProfileStore : IPortForwardProfileStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PortForwardProfile> _items = [];

    public Task<IReadOnlyList<PortForwardProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<PortForwardProfile>>([.. _items.Values]);
    }

    public Task SaveAsync(PortForwardProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _items[profile.Id] = profile;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _items.Remove(id);
        return Task.CompletedTask;
    }
}

public sealed record PortForwardProfileDocument(IReadOnlyList<PortForwardProfile> Profiles);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(PortForwardProfileDocument))]
internal sealed partial class PortForwardProfileJsonContext : JsonSerializerContext
{
}
