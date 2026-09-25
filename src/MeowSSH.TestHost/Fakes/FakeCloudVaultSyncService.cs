using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using Microsoft.AspNetCore.Components;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Stands in for CloudVaultSyncService, which needs a real vault and a running
/// MeowSSHAPI. The merge, conflict-retry and gating rules are tested in Core
/// against real vaults; this reproduces only what the page renders.
/// </summary>
/// <remarks>
/// "syncfails" in the query string makes every sync fail the way a server
/// refusal does, so the page's error path can be seen.
/// </remarks>
public sealed class FakeCloudVaultSyncService(
    IEntitlementService entitlements,
    ICloudVaultBackupService backup,
    NavigationManager navigation) : ICloudVaultSyncService
{
    private readonly bool _fails = new Uri(navigation.Uri).Query.Contains("syncfails", StringComparison.OrdinalIgnoreCase);
    private int _syncs;

    public bool CanSync => entitlements.Has(PremiumFeature.CloudSync);

    public CloudSyncStatus Status { get; private set; } = CloudSyncStatus.Off;

    public event EventHandler? StatusChanged;

    public Task<CloudSyncStatus> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSync) throw new InvalidOperationException("Sync across devices requires MeowSSH Pro Cloud.");
        if (!(await backup.GetStatusAsync(cancellationToken)).Enabled) throw new InvalidOperationException("Turn on cloud backup first.");
        Publish(Status with { Enabled = true });
        await SyncNowAsync(cancellationToken);
    }

    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        Publish(CloudSyncStatus.Off);
        return Task.CompletedTask;
    }

    public Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSync) throw new InvalidOperationException("Sync across devices requires MeowSSH Pro Cloud.");
        if (_fails)
        {
            const string message = "Other devices kept syncing at the same moment. Try again in a minute.";
            Publish(Status with { LastError = message });
            throw new CloudBackupException("sync_conflict", message);
        }

        _syncs++;
        Publish(Status with { LastSyncedAtUtc = new DateTimeOffset(2026, 9, 25, 9, _syncs, 0, TimeSpan.Zero), LastError = null });
        return Task.CompletedTask;
    }

    private void Publish(CloudSyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
