using System.Security.Cryptography;
using System.Text;
using MeowSSH.Core.Security;

namespace MeowSSH.Core.Tests.Security;

public class VaultCryptoTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(VaultCrypto.KeySize);

    [Fact]
    public void SealedValueRoundTripsUnderTheSameKeyAndContext()
    {
        var key = Key();
        var context = VaultCrypto.RecordContext("credential", "abc-123", 1);
        var plaintext = "hunter2"u8.ToArray();

        var sealedValue = VaultCrypto.Seal(key, plaintext, context);
        using var opened = VaultCrypto.Open(key, sealedValue, context);

        Assert.Equal(plaintext, opened.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void SealingTheSamePlaintextTwiceProducesDifferentCiphertext()
    {
        var key = Key();
        var context = VaultCrypto.RecordContext("credential", "abc-123", 1);
        var plaintext = "hunter2"u8.ToArray();

        var first = VaultCrypto.Seal(key, plaintext, context);
        var second = VaultCrypto.Seal(key, plaintext, context);

        // A fresh nonce per message. Without this, identical passwords across
        // records would be visibly identical in the database.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void RecordSealedForOneRecordCannotBeOpenedAsAnother()
    {
        // The attack: someone with write access to the database copies the
        // encrypted credential of a host you trust onto a host they control.
        // Binding the record id into the tag has to make that fail.
        var key = Key();
        var trusted = VaultCrypto.RecordContext("credential", "prod-db", 1);
        var attacker = VaultCrypto.RecordContext("credential", "attacker-box", 1);

        var stolen = VaultCrypto.Seal(key, "prod-password"u8, trusted);

        Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Open(key, stolen, attacker));
    }

    [Fact]
    public void RecordSealedUnderOneSchemaVersionCannotBeOpenedUnderAnother()
    {
        var key = Key();
        var v1 = VaultCrypto.RecordContext("credential", "prod-db", 1);
        var v2 = VaultCrypto.RecordContext("credential", "prod-db", 2);

        var sealedValue = VaultCrypto.Seal(key, "secret"u8, v1);

        Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Open(key, sealedValue, v2));
    }

    [Fact]
    public void TamperingWithCiphertextIsDetected()
    {
        var key = Key();
        var context = VaultCrypto.RecordContext("credential", "abc-123", 1);
        var sealedValue = VaultCrypto.Seal(key, "hunter2"u8, context);

        sealedValue[VaultCrypto.NonceSize] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Open(key, sealedValue, context));
    }

    [Fact]
    public void WrongKeyIsDetected()
    {
        var context = VaultCrypto.RecordContext("credential", "abc-123", 1);
        var sealedValue = VaultCrypto.Seal(Key(), "hunter2"u8, context);

        Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Open(Key(), sealedValue, context));
    }

    [Fact]
    public void TruncatedMessageIsRejectedWithoutThrowingSomethingUseless()
    {
        var ex = Assert.Throws<CryptographicException>(
            () => VaultCrypto.Open(Key(), new byte[4], ReadOnlySpan<byte>.Empty));
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordContextCannotCollideAcrossFieldBoundaries()
    {
        // Length-prefixed, not delimiter-joined: ("ab","c") and ("a","bc") must
        // not produce the same associated data, or those records could be swapped.
        var left = VaultCrypto.RecordContext("ab", "c", 1);
        var right = VaultCrypto.RecordContext("a", "bc", 1);

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void EmptyPlaintextStillAuthenticates()
    {
        var key = Key();
        var context = VaultCrypto.RecordContext("note", "empty", 1);

        var sealedValue = VaultCrypto.Seal(key, ReadOnlySpan<byte>.Empty, context);
        using var opened = VaultCrypto.Open(key, sealedValue, context);

        Assert.Equal(0, opened.Length);
        Assert.Equal(VaultCrypto.Overhead, sealedValue.Length);
    }

    [Fact]
    public void KeyOfTheWrongSizeIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => VaultCrypto.Seal(new byte[16], "x"u8, ReadOnlySpan<byte>.Empty));
        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void LargeValuesRoundTrip()
    {
        var key = Key();
        var context = VaultCrypto.RecordContext("privatekey", "id_ed25519", 1);
        var plaintext = Encoding.UTF8.GetBytes(new string('k', 64 * 1024));

        var sealedValue = VaultCrypto.Seal(key, plaintext, context);
        using var opened = VaultCrypto.Open(key, sealedValue, context);

        Assert.Equal(plaintext, opened.ReadOnlySpan.ToArray());
    }
}
