using MeowSSH.Core.Model;
using MeowSSH.Core.Security;

namespace MeowSSH.Core.Storage;

/// <summary>
/// Everything one device's vault holds, in memory and decrypted.
/// </summary>
/// <remarks>
/// <para>
/// The whole vault is read and written at once. A phone's host list is tens of
/// rows, not thousands, and loading it whole buys two things worth more than
/// incremental reads: a save is atomic, so a process killed mid-write cannot
/// leave half a vault behind; and the file's shape does not leak how many hosts
/// exist, because each section is sealed as one blob.
/// </para>
/// <para>
/// <see cref="Revision"/> counts saves, and each section's ciphertext is bound to
/// it. That is what stops a rollback: an attacker who kept yesterday's file
/// cannot paste its host section into today's, because the tag will not verify
/// against the current revision.
/// </para>
/// </remarks>
public sealed record VaultDocument
{
    /// <summary>The only schema this build writes. A newer file is refused rather than guessed at.</summary>
    public const int SchemaVersion = 5;

    /// <summary>
    /// Identifies the device that wrote this vault, so sync can break ties between
    /// two edits made in the same instant.
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>How many times this vault has been saved. Bound into every section's tag.</summary>
    public long Revision { get; init; }

    /// <summary>
    /// Every encrypted copy of the master key: one per way in. Stored in the
    /// clear part of the file because each is already sealed under its own
    /// wrapping key, and because the recovery copy has to be readable when the
    /// device key is gone.
    /// </summary>
    public IReadOnlyList<WrappedVaultKey> WrappedKeys { get; init; } = [];

    public IReadOnlyList<HostRecord> Hosts { get; init; } = [];

    public IReadOnlyList<CredentialRecord> Credentials { get; init; } = [];

    public static VaultDocument CreateEmpty(string deviceId) => new() { DeviceId = deviceId };

    /// <summary>Hosts the user has not deleted, newest connection first.</summary>
    public IReadOnlyList<HostRecord> LiveHosts =>
        [.. Hosts.Where(h => !h.IsDeleted).OrderByDescending(h => h.LastConnectedAt ?? DateTimeOffset.MinValue).ThenBy(h => h.Label)];

    public IReadOnlyList<CredentialRecord> LiveCredentials =>
        [.. Credentials.Where(c => !c.IsDeleted).OrderBy(c => c.Label)];

    public VaultDocument WithHost(HostRecord host, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(host);
        var previous = Hosts.FirstOrDefault(h => h.Id == host.Id);
        var stamped = host with
        {
            Revision = (previous?.Revision ?? 0) + 1,
            UpdatedAt = now,
            OriginDeviceId = DeviceId,
        };
        return this with { Hosts = Replace(Hosts, stamped, h => h.Id == host.Id) };
    }

    public VaultDocument WithoutHost(Guid hostId, DateTimeOffset now)
    {
        var existing = Hosts.FirstOrDefault(h => h.Id == hostId);
        if (existing is null || existing.IsDeleted) return this;
        var tombstone = existing with
        {
            DeletedAt = now,
            Revision = existing.Revision + 1,
            UpdatedAt = now,
            OriginDeviceId = DeviceId,
        };
        return this with { Hosts = Replace(Hosts, tombstone, h => h.Id == hostId) };
    }

    public VaultDocument WithCredential(CredentialRecord credential, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var previous = Credentials.FirstOrDefault(c => c.Id == credential.Id);
        var stamped = credential with
        {
            Revision = (previous?.Revision ?? 0) + 1,
            UpdatedAt = now,
            OriginDeviceId = DeviceId,
        };
        return this with { Credentials = Replace(Credentials, stamped, h => h.Id == credential.Id) };
    }

    public VaultDocument WithoutCredential(Guid credentialId, DateTimeOffset now)
    {
        var existing = Credentials.FirstOrDefault(c => c.Id == credentialId);
        if (existing is null || existing.IsDeleted) return this;
        var tombstone = existing with
        {
            Secret = [],
            Passphrase = null,
            DeletedAt = now,
            Revision = existing.Revision + 1,
            UpdatedAt = now,
            OriginDeviceId = DeviceId,
        };
        return this with { Credentials = Replace(Credentials, tombstone, h => h.Id == credentialId) };
    }

    public VaultDocument WithWrappedKey(WrappedVaultKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return this with { WrappedKeys = Replace(WrappedKeys, key, k => k.Id == key.Id) };
    }

    public VaultDocument WithoutWrappedKey(string keyId) =>
        this with { WrappedKeys = [.. WrappedKeys.Where(k => k.Id != keyId)] };

    public WrappedVaultKey? FindWrappedKey(KeyWrapMethod method) =>
        WrappedKeys.FirstOrDefault(k => k.Method == method);

    private static T[] Replace<T>(IReadOnlyList<T> items, T replacement, Func<T, bool> matches)
    {
        var index = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (!matches(items[i])) continue;
            index = i;
            break;
        }

        if (index < 0) return [.. items, replacement];
        var copy = items.ToArray();
        copy[index] = replacement;
        return copy;
    }
}
