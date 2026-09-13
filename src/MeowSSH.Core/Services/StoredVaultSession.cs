using System.Security.Cryptography;
using MeowSSH.Core.Security;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Services;

/// <summary>
/// The real vault session: a persisted store, the device's key store, and the
/// biometric prompt in front of both.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Core rather than in the Android head because none of this is
/// Android-specific. What the platform supplies is a hardware key and a prompt,
/// both behind interfaces; the decisions — when to prompt, what a retired key
/// means, which failures are recoverable — are the same everywhere and are worth
/// testing without a device.
/// </para>
/// <para>
/// The biometric prompt is raised before touching the key store, even though the
/// Keystore would raise its own. Two prompts for one unlock is confusing, and
/// this way the wording is the app's rather than the platform's default.
/// </para>
/// </remarks>
public sealed class StoredVaultSession(
    VaultStore store,
    IDeviceKeyStore keyStore,
    IBiometricGate biometrics,
    IDeviceIdentity identity,
    IKeyDerivation? recoveryKdf = null) : IVaultSession
{
    private bool _initialized;

    public VaultState State { get; private set; } = VaultState.NotCreated;

    /// <summary>
    /// Two minutes in the background before the key is zeroed.
    /// </summary>
    /// <remarks>
    /// Short enough that a phone left on a table is not an open terminal, long
    /// enough that checking a message mid-session does not mean authenticating
    /// again. Anything longer turns the biometric gate into a formality.
    /// </remarks>
    public TimeSpan AutoLockAfter => TimeSpan.FromMinutes(2);

    public event EventHandler? StateChanged;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var header = await store.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            Move(VaultState.NotCreated);
            _initialized = true;
            return;
        }

        // A vault whose only remaining way in is the recovery code has to say so
        // up front, rather than after the user has tried a fingerprint that
        // cannot work.
        var hasDeviceKey = header.WrappedKeys.Any(k => k.Method == KeyWrapMethod.DeviceKeyStore)
            && await keyStore.IsAvailableAsync(cancellationToken).ConfigureAwait(false);

        Move(hasDeviceKey ? VaultState.Locked : VaultState.NeedsRecovery);
        _initialized = true;
    }

    public async ValueTask<VaultSetupResult> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (State != VaultState.NotCreated)
            return VaultSetupResult.Failed("A vault already exists on this device.");

        var availability = await biometrics.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (Unusable(availability) is { } reason) return VaultSetupResult.Failed(reason);

        // Authenticating before the vault exists proves the user can get back in
        // later. Creating it first and discovering the sensor does not work would
        // leave a vault only the recovery code could ever open.
        var result = await biometrics.AuthenticateAsync(
            new BiometricPromptOptions(
                Title: "Set up MeowSSH",
                Subtitle: "This is how you will unlock your hosts and keys."),
            cancellationToken).ConfigureAwait(false);

        if (result != BiometricResult.Succeeded)
            return VaultSetupResult.Failed(Explain(result));

        try
        {
            var deviceId = await identity.GetDeviceIdAsync(cancellationToken).ConfigureAwait(false);
            var recoveryCode = await store
                .CreateAsync(keyStore, deviceId, recoveryKdf, cancellationToken)
                .ConfigureAwait(false);
            Move(VaultState.Unlocked);
            return VaultSetupResult.Success(recoveryCode);
        }
        catch (DeviceKeyUnavailableException ex)
        {
            return VaultSetupResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The key store is a platform service reached through bindings, and
            // it can throw types this layer has never heard of. Creating a vault
            // is the first thing a user does, so an unrecognised failure has to
            // become a sentence rather than an unhandled exception that stops
            // the screen repainting.
            return VaultSetupResult.Failed($"The vault could not be created on this device: {ex.Message}");
        }
    }

    public async ValueTask<VaultUnlockResult> UnlockAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (State == VaultState.NotCreated)
            return VaultUnlockResult.Failed(State, "There is no vault on this device yet.");

        var availability = await biometrics.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (Unusable(availability) is { } reason) return VaultUnlockResult.Failed(State, reason);

        var result = await biometrics.AuthenticateAsync(
            new BiometricPromptOptions(
                Title: "Unlock MeowSSH",
                Subtitle: "Your hosts and keys are encrypted on this device."),
            cancellationToken).ConfigureAwait(false);

        if (result != BiometricResult.Succeeded)
            return VaultUnlockResult.Failed(State, Explain(result));

        try
        {
            await store.UnlockWithDeviceKeyAsync(keyStore, cancellationToken).ConfigureAwait(false);
            return Move(VaultState.Unlocked, VaultUnlockResult.Success());
        }
        catch (DeviceKeyUnavailableException ex) when (ex.RequiresRecovery)
        {
            // Not a malfunction: the enrolment guard did what it exists for. The
            // words have to say that, because a user who thinks the app lost their
            // data will uninstall it.
            return Move(VaultState.NeedsRecovery, VaultUnlockResult.Failed(VaultState.NeedsRecovery, ex.Message));
        }
        catch (DeviceKeyUnavailableException ex)
        {
            return VaultUnlockResult.Failed(State, ex.Message);
        }
        catch (CryptographicException)
        {
            return VaultUnlockResult.Failed(State,
                "The vault on this device is damaged and could not be opened. Restore it from a backup.");
        }
    }

    public async ValueTask<VaultUnlockResult> UnlockWithRecoveryCodeAsync(
        string recoveryCode, CancellationToken cancellationToken = default)
    {
        if (!_initialized) await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (State == VaultState.NotCreated)
            return VaultUnlockResult.Failed(State, "There is no vault on this device yet.");

        if (!RecoveryCode.IsWellFormed(recoveryCode))
            return VaultUnlockResult.Failed(State, "That code is not complete. Check for a missing character.");

        try
        {
            await store.UnlockWithRecoveryCodeAsync(recoveryCode, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            return VaultUnlockResult.Failed(State, "That recovery code does not match this vault.");
        }
        catch (FormatException)
        {
            return VaultUnlockResult.Failed(State, "That code contains a character this kind of code never uses.");
        }

        // Having got in, give biometric unlock back rather than leaving the user
        // typing the code forever. A failure here is not a failed unlock: the
        // vault is open, and the next launch simply asks for the code again.
        await TryRebindDeviceKeyAsync(cancellationToken).ConfigureAwait(false);
        return Move(VaultState.Unlocked, VaultUnlockResult.Success());
    }

    public ValueTask LockAsync()
    {
        store.Lock();
        Move(VaultState.Locked);
        return ValueTask.CompletedTask;
    }

    private async ValueTask TryRebindDeviceKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await keyStore.IsAvailableAsync(cancellationToken).ConfigureAwait(false)) return;
            var availability = await biometrics.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            if (availability != BiometricAvailability.Available) return;
            await store.RebindDeviceKeyAsync(keyStore, cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceKeyUnavailableException)
        {
            // The recovery itself succeeded, which is what the user asked for.
        }
    }

    private static string? Unusable(BiometricAvailability availability) => availability switch
    {
        BiometricAvailability.NotEnrolled =>
            "Set up a fingerprint, face unlock or a screen lock first, then try again.",
        BiometricAvailability.NoHardware =>
            "This device has no biometric hardware or screen lock MeowSSH can use.",
        BiometricAvailability.TemporarilyUnavailable =>
            "The sensor is busy. Try again in a moment.",
        _ => null,
    };

    private static string Explain(BiometricResult result) => result switch
    {
        BiometricResult.Cancelled => "Unlock cancelled.",
        BiometricResult.LockedOut => "Too many attempts. Wait a moment, or use your recovery code.",
        _ => "That did not match. Try again.",
    };

    private void Move(VaultState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private VaultUnlockResult Move(VaultState state, VaultUnlockResult result)
    {
        Move(state);
        return result;
    }
}

/// <summary>Supplies the stable per-install id that sync attributes edits to.</summary>
public interface IDeviceIdentity
{
    ValueTask<string> GetDeviceIdAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps the install seed in a file beside the vault.
/// </summary>
/// <remarks>
/// Not secret — it identifies a device, it does not authenticate one — so it
/// lives unencrypted. It has to be readable before the vault is open, because the
/// vault's own creation needs it.
/// </remarks>
public sealed class FileDeviceIdentity(string path) : IDeviceIdentity
{
    private string? _cached;

    public async ValueTask<string> GetDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;

        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (existing.Length >= 16) return _cached = VaultStore.DeriveDeviceId(existing);
        }

        var seed = VaultStore.NewInstallSeed();
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(path, seed, cancellationToken).ConfigureAwait(false);
        return _cached = VaultStore.DeriveDeviceId(seed);
    }
}
