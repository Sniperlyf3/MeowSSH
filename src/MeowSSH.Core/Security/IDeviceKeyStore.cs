namespace MeowSSH.Core.Security;

/// <summary>
/// Wraps and unwraps the vault's master key using a key held by the platform's
/// hardware-backed key store — the Android Keystore, ideally inside StrongBox.
/// </summary>
/// <remarks>
/// The interface deliberately exposes wrap and unwrap rather than "give me the
/// key". A Keystore key is non-exportable by design: it lives in the secure
/// element and the OS performs the AES-GCM operation on our behalf. Any API
/// shaped as "fetch the key" could not be implemented against real hardware.
/// </remarks>
public interface IDeviceKeyStore
{
    /// <summary>Whether this device has a usable hardware-backed key store.</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the wrapping key is held in a dedicated security chip (StrongBox)
    /// rather than the main processor's trusted execution environment.
    /// </summary>
    ValueTask<bool> IsStrongBoxBackedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the wrapping key, replacing any existing one.
    /// </summary>
    /// <param name="requireUserAuthentication">
    /// Bind the key to a successful biometric or device-credential check, so it
    /// cannot be used at all until the user authenticates.
    /// </param>
    /// <param name="cancellationToken">Cancels provisioning.</param>
    ValueTask CreateWrappingKeyAsync(bool requireUserAuthentication, CancellationToken cancellationToken = default);

    /// <summary>Encrypts the master key under the device wrapping key.</summary>
    ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> masterKey, CancellationToken cancellationToken = default);

    /// <summary>Decrypts a master key previously produced by <see cref="WrapAsync"/>.</summary>
    /// <exception cref="DeviceKeyUnavailableException">
    /// The wrapping key is gone or no longer usable — see the exception's reason.
    /// </exception>
    ValueTask<SecretBuffer> UnwrapAsync(byte[] wrapped, CancellationToken cancellationToken = default);

    /// <summary>Deletes the wrapping key, rendering every wrapped copy unreadable.</summary>
    ValueTask DeleteWrappingKeyAsync(CancellationToken cancellationToken = default);
}

public enum DeviceKeyUnavailableReason
{
    /// <summary>No wrapping key has been created yet.</summary>
    NotProvisioned,

    /// <summary>
    /// The key was permanently invalidated because the device's biometric
    /// enrolment changed — a new fingerprint or face was added, or all were
    /// removed. The vault must be reopened with the recovery passphrase.
    /// </summary>
    /// <remarks>
    /// This is the protection working as intended, not a failure: it is what
    /// stops someone who can unlock the device from enrolling their own
    /// fingerprint and reading the vault.
    /// </remarks>
    BiometricEnrolmentChanged,

    /// <summary>The user cancelled or failed the authentication prompt.</summary>
    AuthenticationFailed,

    /// <summary>The platform has no usable hardware-backed key store.</summary>
    NotSupported,
}

public sealed class DeviceKeyUnavailableException : Exception
{
    public DeviceKeyUnavailableException(DeviceKeyUnavailableReason reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    public DeviceKeyUnavailableReason Reason { get; }

    /// <summary>
    /// Whether unlocking with the recovery passphrase can recover from this,
    /// as opposed to the user simply needing to try authenticating again.
    /// </summary>
    public bool RequiresRecovery => Reason is DeviceKeyUnavailableReason.BiometricEnrolmentChanged
        or DeviceKeyUnavailableReason.NotProvisioned
        or DeviceKeyUnavailableReason.NotSupported;
}
