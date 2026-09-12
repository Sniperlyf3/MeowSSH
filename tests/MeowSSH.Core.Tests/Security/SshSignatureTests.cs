using System.Security.Cryptography;
using MeowSSH.Core.Security;

namespace MeowSSH.Core.Tests.Security;

public class SshSignatureTests
{
    /// <summary>
    /// The test that matters: a signature produced the way Android produces one,
    /// converted the way the app will convert it, must still verify. Anything
    /// less proves only that bytes moved around.
    /// </summary>
    [Theory]
    [InlineData(32)]   // nistp256, the curve every Keystore implementation provides
    [InlineData(48)]   // nistp384
    [InlineData(66)]   // nistp521
    public void ConvertedSignatureStillVerifies(int fieldSizeBytes)
    {
        var curve = fieldSizeBytes switch
        {
            32 => ECCurve.NamedCurves.nistP256,
            48 => ECCurve.NamedCurves.nistP384,
            _ => ECCurve.NamedCurves.nistP521,
        };
        using var key = ECDsa.Create(curve);
        var message = "authenticate me"u8.ToArray();

        // Android hands back DER, which is what every general-purpose API emits.
        var der = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var ssh = SshSignature.EcdsaDerToSsh(der);
        var fixedWidth = SshSignature.EcdsaSshToFixedWidth(ssh, fieldSizeBytes);

        Assert.True(key.VerifyData(message, fixedWidth, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void ManySignaturesConvertCorrectly()
    {
        // r and s are effectively random per signature, so the sign-padding and
        // leading-zero branches only appear across a run of them. A single
        // round-trip passes even with the padding rule inverted.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        for (var i = 0; i < 200; i++)
        {
            var message = BitConverter.GetBytes(i);
            var der = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

            var fixedWidth = SshSignature.EcdsaSshToFixedWidth(SshSignature.EcdsaDerToSsh(der), 32);

            Assert.True(key.VerifyData(message, fixedWidth, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                $"Signature {i} failed to verify after conversion.");
        }
    }

    [Fact]
    public void ComponentWithHighBitSetGetsASignPaddingByte()
    {
        // DER carries 0x00 before 0xF0 so it does not read as negative. The mpint
        // must carry it too, or the server decodes a negative r and rejects.
        var der = Der(r: [0x00, 0xF0, 0x01], s: [0x02]);

        var ssh = SshSignature.EcdsaDerToSsh(der);

        Assert.Equal([0, 0, 0, 3, 0x00, 0xF0, 0x01, 0, 0, 0, 1, 0x02], ssh);
    }

    [Fact]
    public void ComponentWithoutHighBitGetsNoPadding()
    {
        var der = Der(r: [0x7F, 0xFF], s: [0x01]);

        var ssh = SshSignature.EcdsaDerToSsh(der);

        Assert.Equal([0, 0, 0, 2, 0x7F, 0xFF, 0, 0, 0, 1, 0x01], ssh);
    }

    [Fact]
    public void LeadingZeroesInDerAreNotCarriedIntoTheMpint()
    {
        var der = Der(r: [0x00, 0x00, 0x01], s: [0x01]);

        var ssh = SshSignature.EcdsaDerToSsh(der);

        Assert.Equal([0, 0, 0, 1, 0x01, 0, 0, 0, 1, 0x01], ssh);
    }

    [Fact]
    public void ShortComponentsAreLeftPaddedToTheFieldWidth()
    {
        // A small r is not a malformed signature; it just needs left-padding to
        // the curve's field size, not right-padding, or the value changes.
        var ssh = SshSignature.EcdsaDerToSsh(Der(r: [0x01], s: [0x02]));

        var fixedWidth = SshSignature.EcdsaSshToFixedWidth(ssh, 32);

        Assert.Equal(64, fixedWidth.Length);
        Assert.Equal(0x01, fixedWidth[31]);
        Assert.Equal(0x02, fixedWidth[63]);
        Assert.All(fixedWidth[..31], b => Assert.Equal(0, b));
        Assert.All(fixedWidth[32..63], b => Assert.Equal(0, b));
    }

    [Fact]
    public void LongFormLengthsAreAccepted()
    {
        // A P-521 signature's SEQUENCE exceeds 127 bytes, so its length uses the
        // long form. Rejecting that would break the largest curve only.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        var der = key.SignData("x"u8.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.True((der[1] & 0x80) != 0, "Expected this signature to use a long-form DER length.");

        var ssh = SshSignature.EcdsaDerToSsh(der);

        Assert.NotEmpty(ssh);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x30 })]
    [InlineData(new byte[] { 0x02, 0x01, 0x01 })]                    // INTEGER, not SEQUENCE
    [InlineData(new byte[] { 0x30, 0x7F, 0x02, 0x01, 0x01 })]        // claims more than it holds
    [InlineData(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x01 })]        // only one INTEGER
    public void MalformedInputIsRejected(byte[] malformed)
    {
        Assert.ThrowsAny<CryptographicException>(() => SshSignature.EcdsaDerToSsh(malformed));
    }

    [Fact]
    public void TrailingBytesAfterTheSequenceAreRejected()
    {
        var der = Der(r: [0x01], s: [0x02]).Concat<byte>([0xFF]).ToArray();

        Assert.ThrowsAny<CryptographicException>(() => SshSignature.EcdsaDerToSsh(der));
    }

    [Fact]
    public void ATruncatedSshBlobIsRejected()
    {
        Assert.ThrowsAny<CryptographicException>(
            () => SshSignature.EcdsaSshToFixedWidth([0, 0, 0, 8, 1, 2], 32));
    }

    [Fact]
    public void AComponentWiderThanTheFieldIsRejected()
    {
        // Leading zeroes are trimmed, so an all-zero component is not over-wide.
        // The value has to actually occupy 40 bytes to exceed a 32-byte field.
        byte[] tooWide = [0x01, .. new byte[39]];
        var ssh = SshSignature.EcdsaDerToSsh(Der(r: tooWide, s: [0x01]));

        var ex = Assert.ThrowsAny<CryptographicException>(() => SshSignature.EcdsaSshToFixedWidth(ssh, 32));
        Assert.Contains("wider than", ex.Message);
    }

    /// <summary>Builds a DER SEQUENCE of two INTEGERs with the given contents.</summary>
    private static byte[] Der(byte[] r, byte[] s)
    {
        byte[] Integer(byte[] value) => [0x02, (byte)value.Length, .. value];
        var body = Integer(r).Concat(Integer(s)).ToArray();
        return [0x30, (byte)body.Length, .. body];
    }
}
