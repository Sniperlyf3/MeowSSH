using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;

namespace MeowSSH.E2E;

/// <summary>
/// The Linux run's device key store: an AES-GCM key held in process memory.
/// </summary>
/// <remarks>
/// The emulator run uses the real AndroidDeviceKeyStore instead; this exists so
/// the same scenarios run on Linux, where there is no Keystore. It is never a
/// substitute for the device run's coverage of the Keystore itself.
/// </remarks>
public sealed class SoftwareDeviceKeyStore : IDeviceKeyStore
{
    private byte[]? _key;

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public ValueTask<bool> IsStrongBoxBackedAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask CreateWrappingKeyAsync(bool requireUserAuthentication, CancellationToken cancellationToken = default)
    {
        _key = RandomNumberGenerator.GetBytes(32);
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> masterKey, CancellationToken cancellationToken = default)
    {
        var key = _key ?? throw new DeviceKeyUnavailableException(DeviceKeyUnavailableReason.NotProvisioned, "No wrapping key.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var output = new byte[12 + 16 + masterKey.Length];
        nonce.CopyTo(output, 0);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, masterKey.Span, output.AsSpan(28), output.AsSpan(12, 16));
        return ValueTask.FromResult(output);
    }

    public ValueTask<SecretBuffer> UnwrapAsync(byte[] wrapped, CancellationToken cancellationToken = default)
    {
        var key = _key ?? throw new DeviceKeyUnavailableException(DeviceKeyUnavailableReason.NotProvisioned, "No wrapping key.");
        var plain = SecretBuffer.Allocate(wrapped.Length - 28);
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), plain.Span);
        return ValueTask.FromResult(plain);
    }

    public ValueTask DeleteWrappingKeyAsync(CancellationToken cancellationToken = default)
    {
        _key = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The Linux run's non-exportable SSH key store: P-256 keys in process memory,
/// producing the same public-key and signature encodings AndroidSshHardwareKeyStore does.
/// </summary>
public sealed class SoftwareSshHardwareKeyStore : ISshHardwareKeyStore
{
    private const string SshKeyType = "ecdsa-sha2-nistp256";
    private readonly ConcurrentDictionary<string, ECDsa> _keys = new();

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public ValueTask<SshHardwareKeyInfo> GenerateP256Async(string keyId, CancellationToken cancellationToken = default)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (!_keys.TryAdd(keyId, key))
        {
            key.Dispose();
            throw new InvalidOperationException($"Key '{keyId}' already exists.");
        }
        return ValueTask.FromResult(Info(keyId, key));
    }

    public ValueTask<SshHardwareKeyInfo?> GetInfoAsync(string keyId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_keys.TryGetValue(keyId, out var key) ? Info(keyId, key) : null);

    public ValueTask DeleteAsync(string keyId, CancellationToken cancellationToken = default)
    {
        if (_keys.TryRemove(keyId, out var key)) key.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> SignAsync(string keyId, string algorithm, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(algorithm, SshKeyType, StringComparison.Ordinal))
            throw new NotSupportedException($"P-256 SSH keys cannot sign using '{algorithm}'.");
        if (!_keys.TryGetValue(keyId, out var key))
            throw new InvalidOperationException($"No key '{keyId}'.");
        var der = key.SignData(data.Span, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return ValueTask.FromResult(SshSignature.EcdsaDerToSsh(der));
    }

    private static SshHardwareKeyInfo Info(string keyId, ECDsa key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        var point = new byte[65];
        point[0] = 0x04;
        q.X!.CopyTo(point, 1);
        q.Y!.CopyTo(point, 33);

        using var blob = new MemoryStream();
        WriteString(blob, Encoding.ASCII.GetBytes(SshKeyType));
        WriteString(blob, "nistp256"u8);
        WriteString(blob, point);
        return new SshHardwareKeyInfo(keyId, $"{SshKeyType} {Convert.ToBase64String(blob.ToArray())} meowssh:{keyId}", false, false);
    }

    private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
