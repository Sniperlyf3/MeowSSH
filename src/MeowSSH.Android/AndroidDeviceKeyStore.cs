using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using MeowSSH.Core.Security;

namespace MeowSSH.Android;

/// <summary>
/// Wraps the vault master key with a key held in the Android Keystore, gated on
/// the user authenticating.
/// </summary>
/// <remarks>
/// <para>
/// The wrapping key never leaves the secure element: the OS performs the
/// AES-GCM operation on the app's behalf. That is why
/// <see cref="IDeviceKeyStore"/> exposes wrap and unwrap rather than "give me
/// the key" — no implementation against real hardware could satisfy the latter.
/// </para>
/// <para>
/// The key is bound to biometric enrolment, so adding a fingerprint invalidates
/// it. That is the protection working: it stops someone who can unlock the
/// device from enrolling their own finger and reading the vault. It also means
/// this key cannot be the only way in, which is why the recovery code exists.
/// </para>
/// </remarks>
public sealed class AndroidDeviceKeyStore(string keyAlias = AndroidDeviceKeyStore.DefaultAlias) : IDeviceKeyStore
{
    public const string DefaultAlias = "meowssh.vault.kek";

    private const string Provider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int TagBits = 128;
    private const int NonceBytes = 12;

    /// <summary>
    /// How long the key stays usable after the user authenticates.
    /// </summary>
    /// <remarks>
    /// Long enough for one unlock to finish its work -- creating a vault stretches
    /// a recovery code with Argon2id, which is deliberately slow -- and short
    /// enough that it is not a standing grant.
    /// </remarks>
    private const int AuthenticationWindowSeconds = 30;

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            LoadKeyStore();
            return ValueTask.FromResult(true);
        }
        catch (Exception)
        {
            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<bool> IsStrongBoxBackedAsync(CancellationToken cancellationToken = default)
    {
        // KeyInfo.SecurityLevel is API 31. Below that the platform will not say
        // which kind of hardware holds the key, and guessing would be worse than
        // reporting the weaker answer.
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return ValueTask.FromResult(false);

        try
        {
            var key = LoadKeyStore().GetKey(keyAlias, null);
            if (key is not ISecretKey secretKey) return ValueTask.FromResult(false);
            var factory = SecretKeyFactory.GetInstance(key.Algorithm!, Provider);
            var info = (KeyInfo?)factory?.GetKeySpec(secretKey, Java.Lang.Class.FromType(typeof(KeyInfo)));
            // StrongBox is the dedicated security chip; a trusted execution
            // environment is the main processor's secure world. Both are
            // hardware, and the distinction is worth showing a user who asks.
            return ValueTask.FromResult(info?.SecurityLevel == (int)KeyStoreSecurityLevel.Strongbox);
        }
        catch (Exception)
        {
            return ValueTask.FromResult(false);
        }
    }

    public ValueTask CreateWrappingKeyAsync(bool requireUserAuthentication, CancellationToken cancellationToken = default)
    {
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)
            ?? throw new DeviceKeyUnavailableException(
                DeviceKeyUnavailableReason.NotSupported, "This device has no AES key generator in its key store.");

        var spec = new KeyGenParameterSpec.Builder(
                keyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .SetUserAuthenticationRequired(requireUserAuthentication)!
            // A new fingerprint must not inherit access to an existing vault.
            .SetInvalidatedByBiometricEnrollment(requireUserAuthentication)!;

        if (requireUserAuthentication) spec = AllowUseShortlyAfterAuthenticating(spec);

        // StrongBox is absent on most devices and throws rather than degrading,
        // so it is attempted and then retried without.
        try
        {
            generator.Init(spec.SetIsStrongBoxBacked(true)!.Build());
            generator.GenerateKey();
            return ValueTask.CompletedTask;
        }
        catch (Java.Security.InvalidAlgorithmParameterException)
        {
            // Falls through to the non-StrongBox attempt below, whose own
            // failure is the one worth reporting.
        }
        catch (Exception)
        {
        }

        try
        {
            generator.Init(spec.SetIsStrongBoxBacked(false)!.Build());
            generator.GenerateKey();
            return ValueTask.CompletedTask;
        }
        catch (Java.Security.InvalidAlgorithmParameterException ex)
        {
            // Asking for a key gated on the user when nothing is enrolled to
            // gate it with fails here, not at unlock time. Left as a Java
            // exception it reaches the UI as an unhandled crash on the first
            // screen; as a typed reason it becomes a sentence telling the user
            // to set up a fingerprint. There is also a real race behind this --
            // enrolment can be removed between the availability check and this
            // call -- so the caller's earlier check is not enough on its own.
            throw new DeviceKeyUnavailableException(
                RequiresEnrolment(ex) ? DeviceKeyUnavailableReason.NotEnrolled : DeviceKeyUnavailableReason.NotSupported,
                RequiresEnrolment(ex)
                    ? "Set up a fingerprint, face unlock or a screen lock first, then try again."
                    : "This device cannot create the key MeowSSH needs to protect your vault.",
                ex);
        }
    }

    /// <summary>
    /// Whether the platform refused because nothing is enrolled, rather than
    /// because the parameters were wrong.
    /// </summary>
    /// <remarks>
    /// Matched on the message because the platform reports both through the same
    /// exception type, with the distinction only in the text. Narrow on purpose:
    /// anything unrecognised is reported as unsupported rather than as something
    /// the user can fix, since telling someone to enrol a finger they already
    /// have is worse than admitting the device is the problem.
    /// </remarks>
    private static bool RequiresEnrolment(Java.Security.InvalidAlgorithmParameterException ex) =>
        ex.Message?.Contains("biometric", StringComparison.OrdinalIgnoreCase) == true
        || ex.Message?.Contains("enrolled", StringComparison.OrdinalIgnoreCase) == true
        || ex.Message?.Contains("secure lock screen", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Lets the key be used for a short window after the user authenticates,
    /// rather than only through a cipher bound to the prompt itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the difference between a working unlock and a frozen screen. A
    /// key created with <c>setUserAuthenticationRequired(true)</c> and no
    /// validity window is authenticated <em>per use</em>: the only way to use it
    /// is to hand the initialised <see cref="Cipher"/> to
    /// <c>BiometricPrompt</c> inside a <c>CryptoObject</c> and let the prompt
    /// authorise that exact operation. Calling <c>Cipher.init</c> on such a key
    /// after an ordinary prompt throws <c>UserNotAuthenticatedException</c>,
    /// however recently the user authenticated.
    /// </para>
    /// <para>
    /// A window is taken instead of a CryptoObject because the whole point of
    /// <see cref="IDeviceKeyStore"/> is that authenticating and using the key
    /// are separate steps: the vault authenticates once and then performs
    /// several operations. Threading a cipher through the prompt would push
    /// Android's shape into an interface that has to work on other platforms
    /// too.
    /// </para>
    /// <para>
    /// The window is short on purpose. It starts at the moment of
    /// authentication, not at first use, and it exists to cover one unlock --
    /// not to leave the key usable while the phone sits on a table.
    /// </para>
    /// </remarks>
    private static KeyGenParameterSpec.Builder AllowUseShortlyAfterAuthenticating(KeyGenParameterSpec.Builder spec)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // Raw constants: the generated bindings do not surface these, and
            // the values are fixed by the platform rather than by the binding.
            // AUTH_DEVICE_CREDENTIAL is 1 << 0, AUTH_BIOMETRIC_STRONG is 1 << 1.
            const int authDeviceCredential = 1 << 0;
            const int authBiometricStrong = 1 << 1;
            return spec.SetUserAuthenticationParameters(
                AuthenticationWindowSeconds, authBiometricStrong | authDeviceCredential)!;
        }

        // Deprecated from API 30 but the only option on 28 and 29, which is the
        // range this app still supports.
#pragma warning disable CA1422
        return spec.SetUserAuthenticationValidityDurationSeconds(AuthenticationWindowSeconds)!;
#pragma warning restore CA1422
    }

    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> masterKey, CancellationToken cancellationToken = default)
    {
        var cipher = Cipher.GetInstance(Transformation)
            ?? throw new DeviceKeyUnavailableException(DeviceKeyUnavailableReason.NotSupported, "AES-GCM is unavailable.");

        byte[]? sealedKey;
        try
        {
            cipher.Init(CipherMode.EncryptMode, RequireKey());
            sealedKey = cipher.DoFinal(masterKey.ToArray());
        }
        catch (KeyPermanentlyInvalidatedException ex)
        {
            throw new DeviceKeyUnavailableException(
                DeviceKeyUnavailableReason.BiometricEnrolmentChanged,
                "The key protecting this vault was retired because the device's biometric enrolment changed.", ex);
        }
        catch (UserNotAuthenticatedException ex)
        {
            // Sealing needs the same authentication opening does, and this used
            // to escape as a raw Java exception -- which reached the UI as a
            // dead screen rather than as anything a user could act on.
            throw new DeviceKeyUnavailableException(
                DeviceKeyUnavailableReason.AuthenticationFailed,
                "Authenticate again, then retry.", ex);
        }

        if (sealedKey is null)
            throw new DeviceKeyUnavailableException(DeviceKeyUnavailableReason.NotProvisioned, "The key store returned nothing.");

        // The nonce is generated by the OS and is not recoverable from the key,
        // so it is stored alongside the ciphertext.
        var nonce = cipher.GetIV() ?? throw new DeviceKeyUnavailableException(
            DeviceKeyUnavailableReason.NotSupported, "The key store produced no nonce.");

        var output = new byte[1 + nonce.Length + sealedKey.Length];
        output[0] = (byte)nonce.Length;
        nonce.CopyTo(output, 1);
        sealedKey.CopyTo(output, 1 + nonce.Length);
        return ValueTask.FromResult(output);
    }

    public ValueTask<SecretBuffer> UnwrapAsync(byte[] wrapped, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        if (wrapped.Length < 1 + NonceBytes + 16)
            throw new DeviceKeyUnavailableException(DeviceKeyUnavailableReason.NotProvisioned, "The wrapped key is truncated.");

        var nonceLength = wrapped[0];
        var nonce = wrapped[1..(1 + nonceLength)];
        var ciphertext = wrapped[(1 + nonceLength)..];

        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, RequireKey(), new GCMParameterSpec(TagBits, nonce));
            var plaintext = cipher.DoFinal(ciphertext)
                ?? throw new DeviceKeyUnavailableException(DeviceKeyUnavailableReason.AuthenticationFailed, "Decryption produced nothing.");
            return ValueTask.FromResult(SecretBuffer.TakeOwnershipOf(plaintext));
        }
        catch (KeyPermanentlyInvalidatedException ex)
        {
            // The device's biometric enrolment changed. Not a malfunction: it is
            // the guarantee that a newly added finger cannot reach an existing
            // vault. Only the recovery code gets back in from here.
            throw new DeviceKeyUnavailableException(
                DeviceKeyUnavailableReason.BiometricEnrolmentChanged,
                "The key protecting this vault was retired because the device's biometric enrolment changed.", ex);
        }
        catch (UserNotAuthenticatedException ex)
        {
            throw new DeviceKeyUnavailableException(
                DeviceKeyUnavailableReason.AuthenticationFailed,
                "Authenticate before unlocking the vault.", ex);
        }
    }

    public ValueTask DeleteWrappingKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            LoadKeyStore().DeleteEntry(keyAlias);
        }
        catch (Exception)
        {
            // Already gone is the desired end state.
        }
        return ValueTask.CompletedTask;
    }

    private IKey RequireKey() =>
        LoadKeyStore().GetKey(keyAlias, null)
        ?? throw new DeviceKeyUnavailableException(
            DeviceKeyUnavailableReason.NotProvisioned, "No vault key exists on this device yet.");

    private static KeyStore LoadKeyStore()
    {
        var store = KeyStore.GetInstance(Provider)
            ?? throw new DeviceKeyUnavailableException(DeviceKeyUnavailableReason.NotSupported, "No Android key store.");
        store.Load(null);
        return store;
    }
}
