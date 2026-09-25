using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Services;

/// <summary>
/// What this phone last agreed with the cloud sync slot on. <see cref="Locator"/>
/// is the cloud identity the rest belongs to (a new recovery code starts over);
/// <see cref="ETag"/> the slot version last merged or written, null before the
/// first sync; <see cref="PushedRevision"/> this vault's revision when it last
/// matched the slot, so anything newer is an unpushed edit.
/// </summary>
public sealed record CloudSyncCheckpoint(
    bool Enabled,
    string? Locator,
    string? ETag,
    long PushedRevision,
    DateTimeOffset? LastSyncedAtUtc)
{
    public static CloudSyncCheckpoint Off { get; } = new(false, null, null, 0, null);
}

/// <summary>Not secret: an ETag is a hash of ciphertext and a locator grants nothing on its own.</summary>
public interface ICloudSyncCheckpointStore
{
    Task<CloudSyncCheckpoint> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CloudSyncCheckpoint checkpoint, CancellationToken cancellationToken = default);
}

public sealed class MemoryCloudSyncCheckpointStore : ICloudSyncCheckpointStore
{
    private CloudSyncCheckpoint _checkpoint = CloudSyncCheckpoint.Off;

    public Task<CloudSyncCheckpoint> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_checkpoint);

    public Task SaveAsync(CloudSyncCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        return Task.CompletedTask;
    }
}

public sealed class FileCloudSyncCheckpointStore(string path) : ICloudSyncCheckpointStore
{
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A sync checkpoint file path is required.", nameof(path))
        : path;

    public async Task<CloudSyncCheckpoint> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return CloudSyncCheckpoint.Off;
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync(stream, CloudSyncJsonContext.Default.CloudSyncCheckpoint, cancellationToken).ConfigureAwait(false)
                ?? CloudSyncCheckpoint.Off;
        }
        catch (JsonException)
        {
            // Losing the checkpoint only costs one full fetch-merge-push; the
            // merge is idempotent, so starting over can never lose an edit.
            return CloudSyncCheckpoint.Off;
        }
    }

    public async Task SaveAsync(CloudSyncCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await File.WriteAllBytesAsync(
            temporary,
            JsonSerializer.SerializeToUtf8Bytes(checkpoint, CloudSyncJsonContext.Default.CloudSyncCheckpoint),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }
}

[JsonSerializable(typeof(CloudSyncCheckpoint))]
internal sealed partial class CloudSyncJsonContext : JsonSerializerContext
{
}

/// <summary>What the UI shows. <see cref="LastError"/> is written for the user, and null after a sync that worked.</summary>
public sealed record CloudSyncStatus(bool Enabled, bool Syncing, DateTimeOffset? LastSyncedAtUtc, string? LastError)
{
    public static CloudSyncStatus Off { get; } = new(false, false, null, null);
}

/// <summary>
/// <see cref="Debounce"/>: how long after an edit or unlock to wait, so a burst
/// of edits is one sync; null never syncs on its own. <see cref="Interval"/>:
/// how often an open vault checks for other phones' edits; null never.
/// </summary>
public sealed record CloudSyncSchedule(TimeSpan? Debounce, TimeSpan? Interval)
{
    public static CloudSyncSchedule Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5));
}

public interface ICloudVaultSyncService
{
    /// <summary>Sync is Pro Cloud. Without it sync pauses; cloud backups stay restorable.</summary>
    bool CanSync { get; }

    CloudSyncStatus Status { get; }

    event EventHandler? StatusChanged;

    Task<CloudSyncStatus> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Needs cloud backup on for the current vault: sync shares its recovery-code identity.</summary>
    Task EnableAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops syncing on this phone. The cloud copy and other phones are left alone.</summary>
    Task DisableAsync(CancellationToken cancellationToken = default);

    /// <exception cref="CloudBackupException">The service refused or could not be reached.</exception>
    /// <exception cref="InvalidOperationException">Sync is not possible right now; the message says why.</exception>
    Task SyncNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps every phone that restored the same vault in step, through the
/// server's compare-and-swap sync slot.
/// </summary>
/// <remarks>
/// <para>
/// One round: fetch the slot (free when unchanged), merge it into the open
/// vault record by record (<see cref="VaultMerge"/>), and push this phone's
/// vault only if it has edits the slot does not. The push names the version it
/// merged; if another phone wrote in between the server refuses it, and the
/// round starts over with that phone's copy merged in. No phone ever
/// overwrites edits it has not seen, and the server never needs to read what
/// it stores.
/// </para>
/// <para>
/// What is uploaded is the vault file exactly as it sits on disk, as with a
/// cloud backup: nothing here decrypts hosts or keys for the network.
/// </para>
/// </remarks>
public sealed class CloudVaultSyncService : ICloudVaultSyncService, IDisposable
{
    private const int MaxAttempts = 3;

    private readonly VaultStore _vault;
    private readonly IVaultStorage _storage;
    private readonly ICloudBackupApi _api;
    private readonly ICloudEntitlementGrantSource _grants;
    private readonly ICloudBackupCredentialStore _credentials;
    private readonly ICloudSyncCheckpointStore _checkpoints;
    private readonly IDeviceIdentity _identity;
    private readonly IEntitlementService _entitlements;
    private readonly CloudSyncSchedule _schedule;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _autoGate = new();
    private readonly ITimer? _timer;
    private CancellationTokenSource? _debounce;
    private bool _disposed;

    public CloudVaultSyncService(
        VaultStore vault,
        IVaultStorage storage,
        ICloudBackupApi api,
        ICloudEntitlementGrantSource grants,
        ICloudBackupCredentialStore credentials,
        ICloudSyncCheckpointStore checkpoints,
        IDeviceIdentity identity,
        IEntitlementService entitlements,
        CloudSyncSchedule? schedule = null,
        TimeProvider? timeProvider = null)
    {
        _vault = vault;
        _storage = storage;
        _api = api;
        _grants = grants;
        _credentials = credentials;
        _checkpoints = checkpoints;
        _identity = identity;
        _entitlements = entitlements;
        _schedule = schedule ?? CloudSyncSchedule.Default;
        _time = timeProvider ?? TimeProvider.System;

        // Unlocking raises Changed too, so opening the app is itself a sync:
        // the vault a user sees first already has the other phones' edits.
        _vault.Changed += OnVaultChanged;
        if (_schedule.Interval is { } interval)
            _timer = _time.CreateTimer(static state => ((CloudVaultSyncService)state!).RequestAutoSync(), this, interval, interval);
    }

    public bool CanSync => _entitlements.Has(PremiumFeature.CloudSync);

    public CloudSyncStatus Status { get; private set; } = CloudSyncStatus.Off;

    public event EventHandler? StatusChanged;

    /// <summary>The auto-sync in flight; completed when none is.</summary>
    internal Task AutoSyncTask { get; private set; } = Task.CompletedTask;

    /// <summary>For tests: waits out automatic syncs, including any one of them set off.</summary>
    internal async Task WhenAutoSyncIdleAsync()
    {
        Task current;
        do
        {
            current = AutoSyncTask;
            await current.ConfigureAwait(false);
        }
        while (!ReferenceEquals(current, AutoSyncTask));
    }

    public async Task<CloudSyncStatus> LoadAsync(CancellationToken cancellationToken = default)
    {
        var checkpoint = await _checkpoints.LoadAsync(cancellationToken).ConfigureAwait(false);
        Publish(Status with { Enabled = checkpoint.Enabled, LastSyncedAtUtc = checkpoint.LastSyncedAtUtc });
        return Status;
    }

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        RequireCanSync();
        var credential = await CloudVaultBackupService.RequireCurrentCredentialAsync(_credentials, _storage, cancellationToken).ConfigureAwait(false);
        await _checkpoints.SaveAsync(new CloudSyncCheckpoint(true, credential.Locator, null, 0, null), cancellationToken).ConfigureAwait(false);
        Publish(new CloudSyncStatus(Enabled: true, Syncing: false, LastSyncedAtUtc: null, LastError: null));
        await SyncNowAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        // Behind the sync gate: a sync already running ends by saving its
        // checkpoint, which still says "on", and would otherwise switch sync
        // straight back on a moment after the user turned it off.
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _checkpoints.SaveAsync(CloudSyncCheckpoint.Off, cancellationToken).ConfigureAwait(false);
            Publish(CloudSyncStatus.Off);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish(Status with { Syncing = true });
            var syncedAt = await SyncOnceAsync(cancellationToken).ConfigureAwait(false);
            Publish(Status with { Syncing = false, LastSyncedAtUtc = syncedAt, LastError = null });
        }
        catch (Exception exception) when (exception is CloudBackupException or InvalidOperationException)
        {
            Publish(Status with { Syncing = false, LastError = exception.Message });
            throw;
        }
        catch
        {
            Publish(Status with { Syncing = false });
            throw;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <returns>When the sync completed.</returns>
    private async Task<DateTimeOffset> SyncOnceAsync(CancellationToken cancellationToken)
    {
        RequireCanSync();
        var checkpoint = await _checkpoints.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!checkpoint.Enabled) throw new InvalidOperationException("Sync is turned off on this phone.");
        if (!_vault.IsUnlocked) throw new InvalidOperationException("Unlock the vault to sync it.");

        var credential = await CloudVaultBackupService.RequireCurrentCredentialAsync(_credentials, _storage, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(checkpoint.Locator, credential.Locator, StringComparison.Ordinal))
            checkpoint = new CloudSyncCheckpoint(true, credential.Locator, null, 0, checkpoint.LastSyncedAtUtc);

        var deviceId = await _identity.GetDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // Read before merging: a merge saves and moves the revision on, and
            // must not be mistaken for an edit of this phone's own.
            var hasUnpushedEdits = _vault.Document.Revision != checkpoint.PushedRevision;

            var fetched = await _api.FetchSyncAsync(credential, checkpoint.ETag, cancellationToken).ConfigureAwait(false);
            string? basis;
            switch (fetched.Kind)
            {
                case CloudSyncFetchKind.Changed:
                    await MergeAsync(fetched.Content!, deviceId, cancellationToken).ConfigureAwait(false);
                    basis = fetched.ETag;
                    if (!hasUnpushedEdits)
                    {
                        // The slot already holds everything this phone had (it
                        // was pushed, and every later writer had to merge it),
                        // so after the merge the two agree and nothing is pushed.
                        return await CompleteAsync(checkpoint with { ETag = basis, PushedRevision = _vault.Document.Revision }, cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case CloudSyncFetchKind.Empty when checkpoint.ETag is not null:
                    // This phone had synced, and the copy is gone: someone chose
                    // "Delete every cloud backup". Re-uploading would quietly undo
                    // that, so sync stops here instead and says why.
                    await _checkpoints.SaveAsync(CloudSyncCheckpoint.Off, cancellationToken).ConfigureAwait(false);
                    Publish(Status with { Enabled = false });
                    throw new InvalidOperationException("The synced copy was deleted from the cloud, so sync was turned off on this phone. Turn it on again to upload this vault.");

                case CloudSyncFetchKind.Empty:
                    basis = null;
                    break;

                default:
                    if (!hasUnpushedEdits)
                        return await CompleteAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                    basis = checkpoint.ETag;
                    break;
            }

            var bytes = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("There is no MeowSSH vault on this phone yet.");
            if (bytes.Length > CloudVaultBackupService.MaxCloudBackupBytes)
                throw new CloudBackupException("backup_too_large", $"This vault is larger than the {CloudVaultBackupService.MaxCloudBackupBytes / (1024 * 1024)} MiB cloud limit, so it cannot be synced.");
            // The revision of the bytes actually sent, not of the vault now: an
            // edit that lands during the upload is then still seen as unpushed.
            var sentRevision = VaultFile.ReadHeader(bytes).Revision;
            var grant = await GetGrantAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var etag = await _api.PushSyncAsync(credential, grant, bytes, basis, cancellationToken).ConfigureAwait(false);
                return await CompleteAsync(checkpoint with { ETag = etag, PushedRevision = sentRevision }, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudBackupException exception) when (exception.Code == "sync_conflict")
            {
                // What was merged stays merged; only the push lost the race.
                // Remembering its ETag means the retry downloads just the newer copy.
                checkpoint = checkpoint with { ETag = basis };
            }
        }

        throw new CloudBackupException("sync_conflict", "Other devices kept syncing at the same moment. Try again in a minute.");
    }

    private async Task MergeAsync(byte[] otherCopy, string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            await _vault.MergeAsync(otherCopy, deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException exception)
        {
            throw new CloudBackupException(
                "sync_unreadable",
                "The synced copy in the cloud could not be opened with this vault's key, so nothing on this phone was changed.",
                exception);
        }
        catch (VaultFormatException exception)
        {
            throw new CloudBackupException("sync_unreadable", exception.Message, exception);
        }
    }

    private async Task<DateTimeOffset> CompleteAsync(CloudSyncCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await _checkpoints.SaveAsync(checkpoint with { LastSyncedAtUtc = now }, cancellationToken).ConfigureAwait(false);
        return now;
    }

    private async Task<SignedEntitlementGrant> GetGrantAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _grants.GetPaidGrantAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Security.SecurityException or HttpRequestException)
        {
            throw new CloudBackupException("entitlement_unavailable", $"Your Pro Cloud purchase could not be verified: {exception.Message}", exception);
        }
    }

    private void RequireCanSync()
    {
        if (!CanSync)
            throw new InvalidOperationException("Sync across devices requires MeowSSH Pro Cloud.");
    }

    private void OnVaultChanged(object? sender, EventArgs e)
    {
        if (_schedule.Debounce is null) return;
        // A merge's own save lands here too. Filtering it out with a flag
        // would also swallow a user edit that finishes in the same instant,
        // leaving it unpushed; letting it through costs one 304 check instead.
        if (!_vault.IsUnlocked) return;
        RequestAutoSync();
    }

    private void RequestAutoSync()
    {
        CancellationToken token;
        lock (_autoGate)
        {
            if (_disposed) return;
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            token = _debounce.Token;
            AutoSyncTask = RunAutoSyncAsync(token);
        }
    }

    private async Task RunAutoSyncAsync(CancellationToken debounce)
    {
        try
        {
            if (_schedule.Debounce is { } delay && delay > TimeSpan.Zero)
                await Task.Delay(delay, _time, debounce).ConfigureAwait(false);
            else
                await Task.Yield();
            debounce.ThrowIfCancellationRequested();

            if (!CanSync || !_vault.IsUnlocked) return;
            var checkpoint = await _checkpoints.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (!checkpoint.Enabled) return;
            // Not the debounce token: the next edit cancels the wait, never a
            // sync already talking to the server. That one finishes, and the
            // new request queues behind it on the sync gate.
            await SyncNowAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later edit's request, which will sync instead.
        }
        catch (Exception exception) when (exception is CloudBackupException or InvalidOperationException)
        {
            // Already on Status for the UI. An automatic sync must never take
            // the app down, and there is nobody to hand the exception to.
        }
    }

    private void Publish(CloudSyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_autoGate)
        {
            if (_disposed) return;
            _disposed = true;
            _vault.Changed -= OnVaultChanged;
            _timer?.Dispose();
            _debounce?.Cancel();
            _debounce?.Dispose();
        }
    }
}
