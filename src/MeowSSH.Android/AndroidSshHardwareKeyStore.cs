using System.Buffers.Binary;
using System.Security.Cryptography.X509Certificates;
using Android.Security.Keystore;
using Java.Security;
using Java.Security.Interfaces;
using Java.Security.Spec;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Android;

/// <summary>
/// Generates P-256 SSH keys inside AndroidKeyStore and services SSH signatures
/// without ever exporting the private half.
/// </summary>
public sealed class AndroidSshHardwareKeyStore : ISshHardwareKeyStore
{
    private const string Provider = "AndroidKeyStore";
    private const string AliasPrefix = "meowssh.ssh.";
    private const string SshKeyType = "ecdsa-sha2-nistp256";
    private const string CurveName = "nistp256";
    private const string AndroidCurveName = "secp256r1";
    private const string SignatureAlgorithm = "SHA256withECDSA";
    private const int CoordinateBytes = 32;

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _ = LoadKeyStore();
            _ = KeyPairGenerator.GetInstance(KeyProperties.KeyAlgorithmEc, Provider);
            return ValueTask.FromResult(true);
        }
        catch
        {
            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<SshHardwareKeyInfo> GenerateP256Async(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKeyId(keyId);
        var alias = Alias(keyId);
        var store = LoadKeyStore();
        if (store.ContainsAlias(alias))
            throw new InvalidOperationException("A non-exportable SSH key with this identifier already exists.");

        // StrongBox is preferred when the device supports EC signing there. The
        // platform throws rather than silently degrading, so retry explicitly in
        // the regular AndroidKeyStore and later report what Android actually used.
        try
        {
            Generate(alias, strongBox: true);
        }
        catch (Exception)
        {
            try { store.DeleteEntry(alias); } catch { }
            Generate(alias, strongBox: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            GetInfoCore(keyId) ?? throw new InvalidOperationException("Android created the SSH key but it could not be reopened."));
    }

    public ValueTask<SshHardwareKeyInfo?> GetInfoAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKeyId(keyId);
        return ValueTask.FromResult(GetInfoCore(keyId));
    }

    public ValueTask DeleteAsync(string keyId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKeyId(keyId);
        try { LoadKeyStore().DeleteEntry(Alias(keyId)); }
        catch { /* already absent is the desired state */ }
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> SignAsync(
        string keyId,
        string algorithm,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKeyId(keyId);
        if (!string.Equals(algorithm, SshKeyType, StringComparison.Ordinal))
            throw new NotSupportedException($"Android P-256 SSH keys cannot sign using '{algorithm}'.");

        var privateKey = LoadKeyStore().GetKey(Alias(keyId), null)
            ?? throw new InvalidOperationException("The selected non-exportable SSH key no longer exists on this device.");
        var signer = Signature.GetInstance(SignatureAlgorithm)
            ?? throw new InvalidOperationException("Android does not provide SHA-256 ECDSA signing.");
        signer.InitSign((IPrivateKey)privateKey);
        signer.Update(data.ToArray());
        var der = signer.Sign()
            ?? throw new InvalidOperationException("Android returned no SSH signature.");
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(SshSignature.EcdsaDerToSsh(der));
    }

    private static void Generate(string alias, bool strongBox)
    {
        var generator = KeyPairGenerator.GetInstance(KeyProperties.KeyAlgorithmEc, Provider)
            ?? throw new InvalidOperationException("AndroidKeyStore has no EC key-pair generator.");
        var builder = new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Sign | KeyStorePurpose.Verify)
            .SetAlgorithmParameterSpec(new ECGenParameterSpec(AndroidCurveName))!
            .SetDigests(KeyProperties.DigestSha256)!;

        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            builder = builder.SetIsStrongBoxBacked(strongBox)!;

        generator.Initialize(builder.Build());
        _ = generator.GenerateKeyPair()
            ?? throw new InvalidOperationException("AndroidKeyStore returned no SSH key pair.");
    }

    private static SshHardwareKeyInfo? GetInfoCore(string keyId)
    {
        var store = LoadKeyStore();
        var alias = Alias(keyId);
        if (!store.ContainsAlias(alias)) return null;

        var certificate = store.GetCertificate(alias)
            ?? throw new InvalidOperationException("The selected AndroidKeyStore entry has no public certificate.");

        var privateKey = store.GetKey(alias, null);
        if (privateKey is not IPrivateKey typedPrivateKey)
            throw new InvalidOperationException("The selected AndroidKeyStore entry has no signing key.");

        var (hardwareBacked, strongBoxBacked) = GetBacking(typedPrivateKey);
        return new SshHardwareKeyInfo(
            keyId,
            ToOpenSshPublicKey(certificate.GetEncoded(), keyId),
            hardwareBacked,
            strongBoxBacked);
    }

    private static (bool HardwareBacked, bool StrongBoxBacked) GetBacking(IPrivateKey privateKey)
    {
        try
        {
            var factory = KeyFactory.GetInstance(privateKey.Algorithm!, Provider);
            var info = (KeyInfo?)factory?.GetKeySpec(privateKey, Java.Lang.Class.FromType(typeof(KeyInfo)));
            if (info is null) return (false, false);

            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var strongBox = info.SecurityLevel == (int)KeyStoreSecurityLevel.Strongbox;
                var hardware = strongBox || info.SecurityLevel == (int)KeyStoreSecurityLevel.TrustedEnvironment;
                return (hardware, strongBox);
            }

#pragma warning disable CA1422
            return (info.IsInsideSecureHardware, false);
#pragma warning restore CA1422
        }
        catch
        {
            // Security metadata is informative, never a reason to pretend a
            // working non-exportable key is stronger than Android can prove.
            return (false, false);
        }
    }

    private static string ToOpenSshPublicKey(byte[] certificateDer, string keyId)
    {
        // Android's Java binding does not consistently expose the certificate's
        // EC public key as IECPublicKey on every runtime. Decode the standard
        // X.509 SubjectPublicKeyInfo through .NET instead; this exports public
        // coordinates only and never touches the AndroidKeyStore private key.
        using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
        using var publicKey = certificate.GetECDsaPublicKey()
            ?? throw new InvalidOperationException("The selected AndroidKeyStore entry is not an EC key.");
        var parameters = publicKey.ExportParameters(includePrivateParameters: false);
        var x = FixedCoordinate(parameters.Q.X);
        var y = FixedCoordinate(parameters.Q.Y);

        var q = new byte[1 + CoordinateBytes * 2];
        q[0] = 0x04;
        x.CopyTo(q, 1);
        y.CopyTo(q, 1 + CoordinateBytes);

        var type = System.Text.Encoding.ASCII.GetBytes(SshKeyType);
        var curve = System.Text.Encoding.ASCII.GetBytes(CurveName);
        var blob = new byte[4 + type.Length + 4 + curve.Length + 4 + q.Length];
        var offset = 0;
        offset += WriteString(blob.AsSpan(offset), type);
        offset += WriteString(blob.AsSpan(offset), curve);
        _ = WriteString(blob.AsSpan(offset), q);

        return $"{SshKeyType} {Convert.ToBase64String(blob)} meowssh:{keyId}";
    }

    private static int WriteString(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value.Length);
        value.CopyTo(destination[4..]);
        return 4 + value.Length;
    }

    private static byte[] FixedCoordinate(byte[]? coordinate)
    {
        if (coordinate is null || coordinate.Length == 0 || coordinate.Length > CoordinateBytes)
            throw new InvalidOperationException("Android returned an invalid P-256 public coordinate.");

        if (coordinate.Length == CoordinateBytes) return coordinate;
        var output = new byte[CoordinateBytes];
        coordinate.CopyTo(output, CoordinateBytes - coordinate.Length);
        return output;
    }

    private static string Alias(string keyId) => AliasPrefix + keyId;

    private static void ValidateKeyId(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (keyId.Length > 128 || keyId.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("Hardware SSH key identifiers may contain only letters, numbers, '.', '-' and '_'.", nameof(keyId));
    }

    private static KeyStore LoadKeyStore()
    {
        var store = KeyStore.GetInstance(Provider)
            ?? throw new InvalidOperationException("AndroidKeyStore is unavailable.");
        store.Load(null);
        return store;
    }
}
