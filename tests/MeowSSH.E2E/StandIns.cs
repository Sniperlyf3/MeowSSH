using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using MeowSSH.UI.Services;

namespace MeowSSH.E2E;

// Stand-ins for the platform services that need a person, a store or a system
// UI. Each one is the smallest honest answer: a biometric prompt the user
// approves, a store with nothing to sell, secure storage that lives in memory.
// Nothing here stands in for business logic -- if a scenario needed one of
// these to pretend harder than this, it would be testing the stand-in.

/// <summary>The user approves every biometric prompt.</summary>
public sealed class ApprovingBiometricGate : IBiometricGate
{
    public int Prompts { get; private set; }

    public ValueTask<BiometricAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(BiometricAvailability.Available);

    public ValueTask<BiometricResult> AuthenticateAsync(BiometricPromptOptions options, CancellationToken cancellationToken = default)
    {
        Prompts++;
        return ValueTask.FromResult(BiometricResult.Succeeded);
    }
}

/// <summary>
/// The entitlement a scenario runs under, standing in for the licensing API so
/// a run never depends on a deployed backend. Mirrors the app's
/// AlwaysProEntitlementGrantProvider, but settable, so a scenario can also
/// prove a Free user is refused.
/// </summary>
public sealed class FixedEntitlementGrantProvider(EntitlementTier tier) : IEntitlementGrantProvider
{
    public EntitlementTier Tier { get; set; } = tier;

    private EntitlementSnapshot Snapshot() => Tier == EntitlementTier.Free
        ? EntitlementSnapshot.Free(DateTimeOffset.UtcNow)
        : new(Tier, EntitlementSource.Promotional, DateTimeOffset.UtcNow, null, "e2e");

    public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());

    public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
}

public sealed class InMemoryEntitlementCache : IEntitlementCache
{
    private EntitlementSnapshot? _value;
    public Task<EntitlementSnapshot?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_value);
    public Task SaveAsync(EntitlementSnapshot entitlement, CancellationToken cancellationToken = default) { _value = entitlement; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken cancellationToken = default) { _value = null; return Task.CompletedTask; }
}

public sealed class NoStorePurchaseService : IStorePurchaseService
{
    public Task<IReadOnlyList<StoreProduct>> GetProductsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoreProduct>>([]);
    public Task<IReadOnlyList<StorePurchase>> GetPurchasesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StorePurchase>>([]);
    public Task<StorePurchase?> PurchaseAsync(string productId, string? basePlanId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<StorePurchase?>(null);
}

public sealed class NoPlayIntegrityService : IPlayIntegrityService
{
    public Task<string?> RequestTokenAsync(string nonce, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

/// <summary>
/// File pickers and the Downloads folder, as a directory. A scenario sets
/// <see cref="NextPick"/> to choose what the "user" picks.
/// </summary>
public sealed class DirectoryLocalFileTransferService(string root) : ILocalFileTransferService
{
    public string Root { get; } = Directory.CreateDirectory(root).FullName;
    public string PublishedDirectory => Directory.CreateDirectory(Path.Combine(Root, "published")).FullName;
    public LocalFileSelection? NextPick { get; set; }

    public Task<LocalFileSelection?> PickFileAsync(CancellationToken cancellationToken = default)
    {
        var pick = NextPick;
        NextPick = null;
        return Task.FromResult(pick);
    }

    public string CreateTemporaryPath(string suggestedName) =>
        Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "tmp")).FullName, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(suggestedName));

    public Task<string> PublishDownloadAsync(string temporaryPath, string suggestedName, CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(PublishedDirectory, Path.GetFileName(suggestedName));
        File.Move(temporaryPath, destination, overwrite: true);
        return Task.FromResult(destination);
    }

    public void DeleteTemporaryFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

public sealed class EmptyThirdPartyNoticeProvider : IThirdPartyNoticeProvider
{
    public ValueTask<string> GetNoticesAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult("");
}

public sealed class NoQrScanner : IQrScanner
{
    public Task<string?> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

/// <summary>No USB serial adapters attached.</summary>
public sealed class NoSerialDeviceService : ISerialDeviceService
{
    public ValueTask<IReadOnlyList<SerialDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<SerialDeviceInfo>>([]);

    public Task<ITerminalSession> OpenAsync(string deviceId, int baudRate, int dataBits, SerialStopBits stopBits, SerialParity parity, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No serial device is attached in an E2E run.");
}

public sealed class InMemoryCloudBackupCredentialStore : ICloudBackupCredentialStore
{
    private CloudBackupCredential? _value;
    public Task<CloudBackupCredential?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_value);
    public Task SaveAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default) { _value = credential; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken cancellationToken = default) { _value = null; return Task.CompletedTask; }
}

public sealed class InMemorySecretStore : IMonitorOwnerSecretStore, ITeamMemberSecretStore
{
    private string? _value;
    public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_value);
    public Task SaveAsync(string secret, CancellationToken cancellationToken = default) { _value = secret; return Task.CompletedTask; }
}

public sealed class GrantedNotificationConsent : INotificationConsent
{
    public Task<bool> EnsureAsync() => Task.FromResult(true);
}

/// <summary>Records command-monitor alerts so a scenario can assert one was raised.</summary>
public sealed class RecordingCommandMonitorAlertSink : ICommandMonitorAlertSink
{
    private readonly List<CommandMonitorAlert> _alerts = [];

    public IReadOnlyList<CommandMonitorAlert> Alerts { get { lock (_alerts) return [.. _alerts]; } }

    public Task NotifyAsync(CommandMonitorAlert alert, CancellationToken cancellationToken = default)
    {
        lock (_alerts) _alerts.Add(alert);
        return Task.CompletedTask;
    }
}
