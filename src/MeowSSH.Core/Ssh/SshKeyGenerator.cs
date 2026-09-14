using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MeowSSH.Core.Ssh;

/// <summary>Creates SSH key pairs in formats accepted by OpenSSH servers and MeowSSH.</summary>
public static class SshKeyGenerator
{
    public static GeneratedSshKey GenerateRsa(string? comment = null, int keySize = 3072)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keySize, 2048);

        using var rsa = RSA.Create(keySize);
        var privateKey = rsa.ExportPkcs8PrivateKeyPem();
        var parameters = rsa.ExportParameters(false);
        var publicKey = BuildOpenSshRsaPublicKey(parameters, comment);
        return new GeneratedSshKey(privateKey, publicKey);
    }

    private static string BuildOpenSshRsaPublicKey(RSAParameters parameters, string? comment)
    {
        ArgumentNullException.ThrowIfNull(parameters.Exponent);
        ArgumentNullException.ThrowIfNull(parameters.Modulus);

        using var stream = new MemoryStream();
        WriteSshString(stream, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteMpInt(stream, parameters.Exponent);
        WriteMpInt(stream, parameters.Modulus);

        var encoded = Convert.ToBase64String(stream.ToArray());
        return string.IsNullOrWhiteSpace(comment)
            ? $"ssh-rsa {encoded}"
            : $"ssh-rsa {encoded} {comment.Trim()}";
    }

    private static void WriteSshString(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
        stream.Write(length);
        stream.Write(value);
    }

    private static void WriteMpInt(Stream stream, ReadOnlySpan<byte> unsignedBigEndian)
    {
        var start = 0;
        while (start < unsignedBigEndian.Length - 1 && unsignedBigEndian[start] == 0) start++;
        var value = unsignedBigEndian[start..];
        var needsLeadingZero = (value[0] & 0x80) != 0;

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)(value.Length + (needsLeadingZero ? 1 : 0))));
        stream.Write(length);
        if (needsLeadingZero) stream.WriteByte(0);
        stream.Write(value);
    }
}

public sealed record GeneratedSshKey(string PrivateKey, string PublicKey);
