using MeowSSH.Core.Licensing;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using Microsoft.AspNetCore.Components;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Stands in for CloudVaultBackupService, which needs a real vault file and a
/// running MeowSSHAPI -- neither exists in the browser host. The real service's
/// rules are tested in Core against real vaults; this reproduces only the
/// outcomes the page renders.
/// </summary>
/// <remarks>
/// "cloudseeded" in the query string starts with two versions already
/// uploaded, as a new phone would find them. A malformed recovery code fails
/// exactly as the real derivation does; a well-formed one is treated as the
/// right code, since the host has no vault to check it against.
/// </remarks>
public sealed class FakeCloudVaultBackupService : ICloudVaultBackupService
{
    private readonly IEntitlementService _entitlements;
    private readonly List<CloudBackupVersion> _versions = [];
    private bool _enabled;
    private int _uploads;

    public FakeCloudVaultBackupService(IEntitlementService entitlements, NavigationManager navigation)
    {
        _entitlements = entitlements;
        if (new Uri(navigation.Uri).Query.Contains("cloudseeded", StringComparison.OrdinalIgnoreCase))
        {
            _versions.Add(Version(new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero), 18_432));
            _versions.Add(Version(new DateTimeOffset(2026, 9, 23, 19, 5, 0, TimeSpan.Zero), 20_480));
        }
    }

    /// <summary>What the last successful restore used, so tests can assert it.</summary>
    public string? RestoredVersionId { get; private set; }

    public bool CanUpload => _entitlements.Has(PremiumFeature.CloudBackup);

    public Task<CloudBackupStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudBackupStatus(_enabled, NeedsRecoveryCode: false));

    public Task EnableAsync(string recoveryCode, CancellationToken cancellationToken = default)
    {
        RequireUpload();
        RequireWellFormed(recoveryCode);
        _enabled = true;
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        _enabled = false;
        return Task.CompletedTask;
    }

    public Task<CloudBackupVersion> BackupNowAsync(CancellationToken cancellationToken = default)
    {
        RequireUpload();
        if (!_enabled) throw new InvalidOperationException("Turn on cloud backup first.");
        _uploads++;
        var version = Version(new DateTimeOffset(2026, 9, 24, 12, _uploads, 0, TimeSpan.Zero), 20_480 + _uploads);
        _versions.Add(version);
        return Task.FromResult(version);
    }

    public Task<IReadOnlyList<CloudBackupVersion>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CloudBackupVersion>>([.. _versions]);

    public Task<IReadOnlyList<CloudBackupVersion>> FindAsync(string recoveryCode, CancellationToken cancellationToken = default)
    {
        RequireWellFormed(recoveryCode);
        return Task.FromResult<IReadOnlyList<CloudBackupVersion>>([.. _versions]);
    }

    public Task RestoreAsync(string recoveryCode, string versionId, CancellationToken cancellationToken = default)
    {
        RequireWellFormed(recoveryCode);
        if (!_versions.Any(version => version.Id == versionId))
            throw new CloudBackupException("not_found", "That backup is no longer stored. Choose another version.");
        RestoredVersionId = versionId;
        _enabled = true;
        return Task.CompletedTask;
    }

    public Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        _versions.Clear();
        _enabled = false;
        return Task.CompletedTask;
    }

    private void RequireUpload()
    {
        if (!CanUpload) throw new InvalidOperationException("Cloud backup requires MeowSSH Pro Cloud.");
    }

    private static void RequireWellFormed(string recoveryCode)
    {
        if (!RecoveryCode.IsWellFormed(recoveryCode))
            throw new InvalidOperationException("That recovery code is not complete. Check for a missing character.");
    }

    private static CloudBackupVersion Version(DateTimeOffset createdAt, long size) =>
        new($"{createdAt.ToUnixTimeMilliseconds():D13}-{size:x16}", createdAt, size, new string('a', 64));
}
