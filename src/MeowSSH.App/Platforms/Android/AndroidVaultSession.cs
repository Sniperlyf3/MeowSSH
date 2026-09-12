using MeowSSH.Core.Security;
using MeowSSH.Core.Services;

namespace MeowSSH.App.Platforms.Android;

/// <summary>
/// Unlocks the vault with the device's biometric prompt, backed by a key the
/// Android Keystore holds.
/// </summary>
/// <remarks>
/// <para>
/// The vault is created on first run and its master key wrapped by the Keystore.
/// There is no persistence layer yet, so the wrapped key lives for the life of
/// the process: the biometric gate, the Keystore key and the key hierarchy are
/// all real, but nothing survives a restart. Storing the wrapped key is the next
/// piece, and it changes only where the bytes go, not how they are protected.
/// </para>
/// <para>
/// Recovery is not wired to a stored code for the same reason. The path exists
/// and is tested in <c>MeowSSH.Core.Tests</c>; this class has nowhere to keep
/// the second wrapped copy yet.
/// </para>
/// </remarks>
internal sealed class AndroidVaultSession(IDeviceKeyStore keyStore, IBiometricGate biometrics) : IVaultSession
{
    private byte[]? _wrappedKey;
    private VaultKeyRing? _keyRing;

    public VaultState State { get; private set; } = VaultState.NotCreated;

    public TimeSpan AutoLockAfter => TimeSpan.FromMinutes(2);

    public event EventHandler? StateChanged;

    public async ValueTask<VaultUnlockResult> UnlockAsync(CancellationToken cancellationToken = default)
    {
        var availability = await biometrics.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (availability is BiometricAvailability.NoHardware or BiometricAvailability.NotEnrolled)
        {
            return VaultUnlockResult.Failed(State,
                availability == BiometricAvailability.NotEnrolled
                    ? "Set up a fingerprint, face unlock or a screen lock first, then try again."
                    : "This device has no biometric hardware MeowSSH can use.");
        }

        var result = await biometrics.AuthenticateAsync(
            new BiometricPromptOptions(
                Title: "Unlock MeowSSH",
                Subtitle: "Your hosts and keys are encrypted on this device."),
            cancellationToken).ConfigureAwait(false);

        if (result != BiometricResult.Succeeded)
        {
            return VaultUnlockResult.Failed(State, result switch
            {
                BiometricResult.Cancelled => "Unlock cancelled.",
                BiometricResult.LockedOut => "Too many attempts. Wait a moment, or use your recovery code.",
                _ => "That did not match. Try again.",
            });
        }

        try
        {
            if (_wrappedKey is null)
            {
                await keyStore.CreateWrappingKeyAsync(requireUserAuthentication: true, cancellationToken).ConfigureAwait(false);
                using var fresh = VaultKeyRing.CreateNew();
                var wrapped = await fresh.WrapWithDeviceKeyStoreAsync("device", keyStore, cancellationToken).ConfigureAwait(false);
                _wrappedKey = wrapped.Payload;
            }

            _keyRing = await VaultKeyRing.UnwrapWithDeviceKeyStoreAsync(
                new WrappedVaultKey("device", KeyWrapMethod.DeviceKeyStore, _wrappedKey),
                keyStore, cancellationToken).ConfigureAwait(false);

            return Transition(VaultState.Unlocked, VaultUnlockResult.Success());
        }
        catch (DeviceKeyUnavailableException ex)
        {
            // A retired key is the enrolment guard doing its job, and it needs
            // different words from a failed fingerprint: nothing the user does at
            // the sensor will help.
            return ex.RequiresRecovery
                ? Transition(VaultState.NeedsRecovery, VaultUnlockResult.Failed(VaultState.NeedsRecovery, ex.Message))
                : VaultUnlockResult.Failed(State, ex.Message);
        }
    }

    public ValueTask<VaultUnlockResult> UnlockWithRecoveryCodeAsync(string recoveryCode, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(VaultUnlockResult.Failed(State,
            "Recovery codes need somewhere to store the second wrapped key, which this build does not have yet."));

    public ValueTask LockAsync()
    {
        // Locking zeroes the key material rather than just changing a flag.
        _keyRing?.Dispose();
        _keyRing = null;
        State = _wrappedKey is null ? VaultState.NotCreated : VaultState.Locked;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    private VaultUnlockResult Transition(VaultState state, VaultUnlockResult result)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }
}
