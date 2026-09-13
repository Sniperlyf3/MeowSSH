using System.Security.Cryptography;
using MeowSSH.Core.Security;

namespace MeowSSH.Core.Tests.Fakes;

/// <summary>
/// A key store that behaves like the Android Keystore without the hardware.
/// </summary>
/// <remarks>
/// <para>
/// It is deliberately not a stub that returns its input. The behaviours that
/// matter to the vault are the ones that go wrong: a key that is destroyed when
/// biometric enrolment changes, an unwrap that fails because the user did not
/// authenticate, and a key that cannot be read back out. All three are simulated
/// here, because they are what the recovery path exists for.
/// </para>
/// <para>
/// The wrapping key is ordinary AES-GCM held in this object. That is exactly what
/// the real implementation must never do — which is why this type lives in the
/// test project and not beside the code it stands in for.
/// </para>
/// </remarks>
internal sealed class FakeDeviceKeyStore : IDeviceKeyStore
{
    private byte[]? _wrappingKey;

    /// <summary>Set to make every unwrap fail the way a cancelled prompt does.</summary>
    public bool AuthenticationFails { get; set; }

    /// <summary>Set to make the store report itself unavailable.</summary>
    public bool IsUnavailable { get; set; }

    public bool StrongBoxBacked { get; set; } = true;

    /// <summary>How many times a wrapping key has been created. Rebinding must make a new one.</summary>
    public int KeysCreated { get; private set; }

    public bool RequiresUserAuthentication { get; private set; }

    /// <summary>
    /// Destroys the wrapping key the way enrolling a new fingerprint does.
    /// </summary>
    public void SimulateBiometricEnrolmentChange() => _wrappingKey = null;

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(!IsUnavailable);

    public ValueTask<bool> IsStrongBoxBackedAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StrongBoxBacked && _wrappingKey is not null);

    public ValueTask CreateWrappingKeyAsync(bool requireUserAuthentication, CancellationToken cancellationToken = default)
    {
        _wrappingKey = RandomNumberGenerator.GetBytes(32);
        RequiresUserAuthentication = requireUserAuthentication;
        KeysCreated++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> masterKey, CancellationToken cancellationToken = default)
    {
        var key = _wrappingKey ?? throw new DeviceKeyUnavailableException(
            DeviceKeyUnavailableReason.NotProvisioned, "No wrapping key exists yet.");
        return ValueTask.FromResult(VaultCrypto.Seal(key, masterKey.Span, Context));
    }

    public ValueTask<SecretBuffer> UnwrapAsync(byte[] wrapped, CancellationToken cancellationToken = default)
    {
        if (AuthenticationFails)
            throw new DeviceKeyUnavailableException(
                DeviceKeyUnavailableReason.AuthenticationFailed, "Authenticate before unlocking the vault.");

        if (_wrappingKey is null)
            throw new DeviceKeyUnavailableException(
                DeviceKeyUnavailableReason.BiometricEnrolmentChanged,
                "The key protecting this vault was retired because the device's biometric enrolment changed.");

        return ValueTask.FromResult(VaultCrypto.Open(_wrappingKey, wrapped, Context));
    }

    public ValueTask DeleteWrappingKeyAsync(CancellationToken cancellationToken = default)
    {
        _wrappingKey = null;
        return ValueTask.CompletedTask;
    }

    private static ReadOnlySpan<byte> Context => "fake-device-key"u8;
}
