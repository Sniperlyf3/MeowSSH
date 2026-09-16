using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MeowSSH.Android;
using MeowSSH.Core.Security;

namespace MeowSSH.Device.Tests;

public static class HardwareSshKeyDeviceTests
{
    public static IReadOnlyList<DeviceTest> All =>
    [
        new("AndroidKeyStore P-256 SSH key signs and deletes without export", SignVerifyAndDeleteAsync),
    ];

    private static async Task SignVerifyAndDeleteAsync()
    {
        var store = new AndroidSshHardwareKeyStore();
        Require(await store.IsAvailableAsync(), "AndroidKeyStore SSH keys are unavailable.");

        var keyId = "device-test-" + Guid.NewGuid().ToString("N");
        try
        {
            var created = await store.GenerateP256Async(keyId);
            Require(created.KeyId == keyId, "The generated key id changed.");
            Require(created.PublicKey.StartsWith("ecdsa-sha2-nistp256 ", StringComparison.Ordinal),
                "The generated public key is not OpenSSH P-256.");

            var reopened = await store.GetInfoAsync(keyId);
            Require(reopened is not null, "The generated AndroidKeyStore entry could not be reopened.");
            Require(reopened!.PublicKey == created.PublicKey, "The public key changed after reopening the key store entry.");

            var data = Encoding.UTF8.GetBytes("meowssh hardware key device test");
            var sshSignature = await store.SignAsync(keyId, "ecdsa-sha2-nistp256", data);
            var fixedWidth = SshSignature.EcdsaSshToFixedWidth(sshSignature, 32);

            using var verifier = ECDsa.Create(ParseP256(created.PublicKey));
            Require(verifier.VerifyData(
                    data,
                    fixedWidth,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                "The AndroidKeyStore signature does not verify with the exported public key.");

            await RequireThrowsAsync<NotSupportedException>(
                () => store.SignAsync(keyId, "ssh-rsa", data).AsTask(),
                "The P-256 key accepted an unsupported SSH signature algorithm.");
        }
        finally
        {
            await store.DeleteAsync(keyId);
        }

        Require(await store.GetInfoAsync(keyId) is null, "The AndroidKeyStore entry still exists after deletion.");
        await RequireThrowsAsync<InvalidOperationException>(
            () => store.SignAsync(keyId, "ecdsa-sha2-nistp256", new byte[] { 1, 2, 3 }).AsTask(),
            "A deleted AndroidKeyStore key could still sign.");
    }

    private static ECParameters ParseP256(string openSsh)
    {
        var parts = openSsh.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Require(parts.Length >= 2, "The OpenSSH public key has no base64 payload.");
        Require(parts[0] == "ecdsa-sha2-nistp256", "The OpenSSH key type is not P-256.");

        var blob = Convert.FromBase64String(parts[1]);
        ReadOnlySpan<byte> cursor = blob;
        Require(Encoding.ASCII.GetString(ReadString(ref cursor)) == "ecdsa-sha2-nistp256", "SSH key blob has the wrong key type.");
        Require(Encoding.ASCII.GetString(ReadString(ref cursor)) == "nistp256", "SSH key blob has the wrong curve.");
        var q = ReadString(ref cursor).ToArray();
        Require(cursor.IsEmpty, "SSH key blob contains trailing data.");
        Require(q.Length == 65, "P-256 public point is not 65 bytes.");
        Require(q[0] == 0x04, "P-256 public point is not uncompressed SEC1 format.");

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = q[1..33],
                Y = q[33..65],
            },
        };
    }

    private static ReadOnlySpan<byte> ReadString(ref ReadOnlySpan<byte> cursor)
    {
        Require(cursor.Length >= 4, "SSH string is truncated before its length.");
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(cursor));
        cursor = cursor[4..];
        Require(length >= 0 && length <= cursor.Length, "SSH string length exceeds the remaining payload.");
        var value = cursor[..length];
        cursor = cursor[length..];
        return value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> operation, string message)
        where TException : Exception
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
