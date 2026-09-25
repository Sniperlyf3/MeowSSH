using System.Security.Cryptography;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Fakes;

/// <summary>
/// In-memory MeowSSHAPI: the same ownership rule (the bearer secret must hash
/// to the locator), the same compare-and-swap on the sync slot, and an ETag
/// that is the content's SHA-256 exactly as the server computes it.
/// </summary>
public sealed class FakeCloudBackupApi : ICloudBackupApi
{
    private readonly Dictionary<string, List<(CloudBackupVersion Version, byte[] Content)>> _store = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ETag, byte[] Content)> _sync = new(StringComparer.Ordinal);

    public List<byte[]> Uploads { get; } = [];

    public List<byte[]> SyncPushes { get; } = [];

    public int SyncFetches { get; private set; }

    /// <summary>Runs just before a push is decided, to stage another phone's write in the gap.</summary>
    public Func<Task>? BeforePush { get; set; }

    public Task<CloudBackupVersion> UploadAsync(CloudBackupCredential credential, SignedEntitlementGrant grant, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var list = Authorized(credential);
        var version = new CloudBackupVersion(Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow, content.Length,
            Convert.ToHexStringLower(SHA256.HashData(content.Span)));
        list.Add((version, content.ToArray()));
        Uploads.Add(content.ToArray());
        return Task.FromResult(version);
    }

    public Task<IReadOnlyList<CloudBackupVersion>> ListAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CloudBackupVersion>>([.. Authorized(credential).Select(static item => item.Version)]);

    public Task<byte[]> DownloadAsync(CloudBackupCredential credential, string versionId, CancellationToken cancellationToken = default)
    {
        var match = Authorized(credential).FirstOrDefault(item => item.Version.Id == versionId);
        return match.Content is null
            ? throw new CloudBackupException("not_found", "not found")
            : Task.FromResult(match.Content.ToArray());
    }

    public Task DeleteAllAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default)
    {
        Authorized(credential);
        _store.Remove(credential.Locator);
        _sync.Remove(credential.Locator);
        return Task.CompletedTask;
    }

    public Task<CloudSyncFetch> FetchSyncAsync(CloudBackupCredential credential, string? ifNoneMatch, CancellationToken cancellationToken = default)
    {
        Authorized(credential);
        SyncFetches++;
        if (!_sync.TryGetValue(credential.Locator, out var slot)) return Task.FromResult(CloudSyncFetch.Empty);
        return Task.FromResult(string.Equals(slot.ETag, ifNoneMatch, StringComparison.Ordinal)
            ? CloudSyncFetch.NotModified(slot.ETag)
            : CloudSyncFetch.Changed(slot.ETag, slot.Content.ToArray()));
    }

    public async Task<string> PushSyncAsync(
        CloudBackupCredential credential,
        SignedEntitlementGrant grant,
        ReadOnlyMemory<byte> content,
        string? expectedETag,
        CancellationToken cancellationToken = default)
    {
        Authorized(credential);
        if (BeforePush is { } hook)
        {
            BeforePush = null;
            await hook();
        }

        var current = _sync.TryGetValue(credential.Locator, out var slot) ? slot.ETag : null;
        if (!string.Equals(current, expectedETag, StringComparison.Ordinal))
            throw new CloudBackupException("sync_conflict", "Another device synced at the same moment.");

        var etag = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        _sync[credential.Locator] = (etag, content.ToArray());
        SyncPushes.Add(content.ToArray());
        return etag;
    }

    /// <summary>The synced copy as the server holds it.</summary>
    public byte[]? SyncedCopy(string locator) => _sync.TryGetValue(locator, out var slot) ? slot.Content : null;

    private List<(CloudBackupVersion Version, byte[] Content)> Authorized(CloudBackupCredential credential)
    {
        var secret = Convert.FromBase64String(credential.SecretBase64Url.Replace('-', '+').Replace('_', '/') + "=");
        if (CloudBackupCredential.LocatorFor(secret) != credential.Locator)
            throw new CloudBackupException("unauthorized", "unauthorized");
        if (!_store.TryGetValue(credential.Locator, out var list)) _store[credential.Locator] = list = [];
        return list;
    }
}

public sealed class FakeCloudGrants(EntitlementTier tier) : ICloudEntitlementGrantSource
{
    public Task<SignedEntitlementGrant> GetPaidGrantAsync(CancellationToken cancellationToken = default) =>
        tier >= EntitlementTier.ProCloud
            ? Task.FromResult(new SignedEntitlementGrant("payload", "signature"))
            : throw new InvalidOperationException("No Pro Cloud purchase.");
}

public sealed class FakeTier(EntitlementTier tier) : IEntitlementService
{
    public EntitlementTier Tier { get; set; } = tier;

    public EntitlementSnapshot Current => new(Tier, EntitlementSource.Promotional, DateTimeOffset.UtcNow);

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public bool Has(PremiumFeature feature) => EntitlementPolicy.Allows(Current, feature, DateTimeOffset.UtcNow);
    public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
}
