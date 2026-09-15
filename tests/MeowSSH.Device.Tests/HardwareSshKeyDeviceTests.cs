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
        Assert.True(await store.IsAvailableAsync());

        var keyId = "device-test-" + Guid.NewGuid().ToString("N");
        try
        {
            var created = await store.GenerateP256Async(keyId);
            Assert.Equal(keyId, created.KeyId);
            Assert.StartsWith("ecdsa-sha2-nistp256 ", created.PublicKey, StringComparison.Ordinal);

            var reopened = await store.GetInfoAsync(keyId);
            Assert.NotNull(reopened);
            Assert.Equal(created.PublicKey, reopened!.PublicKey);

            var data = Encoding.UTF8.GetBytes("meowssh hardware key device test");
            var sshSignature = await store.SignAsync(keyId, "ecdsa-sha2-nistp256", data);
            var fixedWidth = SshSignature.EcdsaSshToFixedWidth(sshSignature, 32);

            using var verifier = ECDsa.Create(ParseP256(created.PublicKey));
            Assert.True(verifier.VerifyData(
                data,
                fixedWidth,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

            await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await store.SignAsync(keyId, "ssh-rsa", data));
        }
        finally
        {
            await store.DeleteAsync(keyId);
        }

        Assert.Null(await store.GetInfoAsync(keyId));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SignAsync(keyId, "ecdsa-sha2-nistp256", [1, 2, 3]));
    }

    private static ECParameters ParseP256(string openSsh)
    {
        var parts = openSsh.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 2);
        Assert.Equal("ecdsa-sha2-nistp256", parts[0]);

        var blob = Convert.FromBase64String(parts[1]);
        var cursor = blob.AsSpan();
        Assert.Equal("ecdsa-sha2-nistp256", Encoding.ASCII.GetString(ReadString(ref cursor)));
        Assert.Equal("nistp256", Encoding.ASCII.GetString(ReadString(ref cursor)));
        var q = ReadString(ref cursor).ToArray();
        Assert.Empty(cursor.ToArray());
        Assert.Equal(65, q.Length);
        Assert.Equal(0x04, q[0]);

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
        Assert.True(cursor.Length >= 4);
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(cursor));
        cursor = cursor[4..];
        Assert.True(length >= 0 && length <= cursor.Length);
        var value = cursor[..length];
        cursor = cursor[length..];
        return value;
    }
}
