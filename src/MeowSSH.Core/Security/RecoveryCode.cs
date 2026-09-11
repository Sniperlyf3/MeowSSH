using System.Security.Cryptography;
using System.Text;

namespace MeowSSH.Core.Security;

/// <summary>
/// The one secret a user must keep outside the app: a high-entropy code that
/// wraps a copy of the vault master key, so the vault survives a lost phone.
/// </summary>
/// <remarks>
/// <para>
/// Hardware-backed keys are the right place for day-to-day unlocking, but they
/// are non-exportable and die with the device. Without a second, portable way to
/// unwrap the master key, a broken phone would mean a lost vault. That makes a
/// recovery code mandatory rather than optional.
/// </para>
/// <para>
/// Encoded in Crockford's Base32, which omits I, L, O and U. That removes the
/// pairs people misread when copying a code off a screen onto paper, and lets
/// the parser silently repair the substitutions they make anyway.
/// </para>
/// </remarks>
public static class RecoveryCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int EntropyBytes = 20;   // 160 bits
    private const int GroupSize = 4;

    /// <summary>Generates a fresh recovery code, formatted for the user to write down.</summary>
    public static string Generate()
    {
        var entropy = RandomNumberGenerator.GetBytes(EntropyBytes);
        try
        {
            return Format(Encode(entropy));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    /// <summary>
    /// Turns a code the user typed into the bytes used to derive the wrapping key.
    /// </summary>
    /// <remarks>
    /// Normalises case, strips separators and whitespace, and repairs the classic
    /// transcription slips (O for zero, I or L for one) before decoding. A code
    /// read off a sticky note should not fail because of a hand-written letter.
    /// </remarks>
    /// <exception cref="FormatException">The code contains characters that are not recoverable typos.</exception>
    public static SecretBuffer Parse(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var canonical = Canonicalize(code);
        if (canonical.Length == 0)
            throw new FormatException("Recovery code is empty once separators are removed.");

        var bits = 0;
        var accumulator = 0;
        var output = new List<byte>(EntropyBytes);
        foreach (var c in canonical)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException($"'{c}' is not a valid character in a recovery code.");
            accumulator = (accumulator << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(accumulator >> bits));
                accumulator &= (1 << bits) - 1;
            }
        }
        return SecretBuffer.TakeOwnershipOf(output.ToArray());
    }

    /// <summary>Whether <paramref name="code"/> is well-formed and the expected length.</summary>
    public static bool IsWellFormed(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        try
        {
            var canonical = Canonicalize(code);
            if (canonical.Length != EncodedLength) return false;
            using var _ = Parse(code);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int EncodedLength => (EntropyBytes * 8 + 4) / 5;

    private static string Canonicalize(string code)
    {
        var builder = new StringBuilder(code.Length);
        foreach (var raw in code)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n') continue;
            var c = char.ToUpperInvariant(raw);
            builder.Append(c switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                'U' => 'V',
                _ => c,
            });
        }
        return builder.ToString();
    }

    private static string Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(EncodedLength);
        var bits = 0;
        var accumulator = 0;
        foreach (var b in data)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Alphabet[(accumulator >> bits) & 31]);
                accumulator &= (1 << bits) - 1;
            }
        }
        if (bits > 0) builder.Append(Alphabet[(accumulator << (5 - bits)) & 31]);
        return builder.ToString();
    }

    private static string Format(string encoded)
    {
        var builder = new StringBuilder(encoded.Length + encoded.Length / GroupSize);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0) builder.Append('-');
            builder.Append(encoded[i]);
        }
        return builder.ToString();
    }
}
