using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MeowSSH.Core.Security;

/// <summary>
/// Converts signatures into the wire shapes SSH expects.
/// </summary>
/// <remarks>
/// <para>
/// Only ECDSA needs converting. Android's Keystore returns an ECDSA signature as
/// a DER <c>SEQUENCE { INTEGER r, INTEGER s }</c>, which is what every
/// general-purpose crypto API produces, but SSH wants the two integers as a pair
/// of SSH mpints (RFC 5656 §3.1.2). Handing the DER straight to the signing
/// callback produces a signature the server rejects with nothing in the log
/// explaining why, so the conversion is not optional.
/// </para>
/// <para>
/// RSA and Ed25519 need no conversion: SSH carries the raw PKCS#1 v1.5 signature
/// and the raw 64-byte Ed25519 signature respectively, which is already what the
/// platform hands back.
/// </para>
/// </remarks>
public static class SshSignature
{
    /// <summary>
    /// Rewrites a DER-encoded ECDSA signature as the <c>mpint r || mpint s</c>
    /// blob an SSH <c>ecdsa-sha2-*</c> signature carries.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The input is not a well-formed DER <c>SEQUENCE</c> of two <c>INTEGER</c>s.
    /// </exception>
    public static byte[] EcdsaDerToSsh(ReadOnlySpan<byte> derSignature)
    {
        var reader = new DerReader(derSignature);
        var sequence = reader.ReadSequence();
        var r = sequence.ReadInteger();
        var s = sequence.ReadInteger();
        sequence.ExpectEnd("ECDSA signature SEQUENCE");
        reader.ExpectEnd("ECDSA signature");

        var output = new byte[MpintLength(r) + MpintLength(s)];
        var written = WriteMpint(output, r);
        WriteMpint(output.AsSpan(written), s);
        return output;
    }

    /// <summary>
    /// Splits an SSH ECDSA signature blob back into its two integers, each
    /// left-padded to <paramref name="fieldSizeBytes"/>.
    /// </summary>
    /// <remarks>
    /// The fixed-width pair is the IEEE P1363 form that .NET's own
    /// <c>ECDsa.VerifyData</c> accepts, which is what makes a converted
    /// signature verifiable without a second implementation to check it against.
    /// </remarks>
    public static byte[] EcdsaSshToFixedWidth(ReadOnlySpan<byte> sshSignature, int fieldSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldSizeBytes);

        var output = new byte[fieldSizeBytes * 2];
        var cursor = sshSignature;
        for (var half = 0; half < 2; half++)
        {
            var value = ReadMpint(ref cursor, half == 0 ? "r" : "s");
            if (value.Length > fieldSizeBytes)
                throw new CryptographicException(
                    $"ECDSA signature component is {value.Length} bytes, wider than the {fieldSizeBytes}-byte field.");
            value.CopyTo(output.AsSpan(half * fieldSizeBytes + fieldSizeBytes - value.Length));
        }
        if (!cursor.IsEmpty)
            throw new CryptographicException("Trailing bytes after the ECDSA signature's two components.");
        return output;
    }

    private static ReadOnlySpan<byte> ReadMpint(ref ReadOnlySpan<byte> cursor, string name)
    {
        if (cursor.Length < 4)
            throw new CryptographicException($"ECDSA signature is truncated before component {name}.");
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(cursor);
        cursor = cursor[4..];
        if (length < 0 || length > cursor.Length)
            throw new CryptographicException($"ECDSA signature component {name} claims {length} bytes but the blob is shorter.");

        var value = cursor[..length];
        cursor = cursor[length..];
        // An mpint carries a leading zero only to keep a high bit from reading as
        // a sign bit; it is not part of the value.
        return value.Length > 0 && value[0] == 0 ? value[1..] : value;
    }

    // An mpint is a 4-byte big-endian length followed by the two's-complement
    // big-endian value. A positive number whose top bit is set needs a leading
    // zero byte, or it would decode as negative.
    private static int MpintLength(ReadOnlySpan<byte> value) =>
        4 + value.Length + (NeedsSignPadding(value) ? 1 : 0);

    private static bool NeedsSignPadding(ReadOnlySpan<byte> value) =>
        value.Length > 0 && (value[0] & 0x80) != 0;

    private static int WriteMpint(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        var pad = NeedsSignPadding(value) ? 1 : 0;
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(value.Length + pad));
        if (pad == 1) destination[4] = 0;
        value.CopyTo(destination[(4 + pad)..]);
        return 4 + pad + value.Length;
    }

    /// <summary>
    /// Just enough DER to read an ECDSA signature. Deliberately strict: this
    /// parses attacker-reachable bytes, so anything unexpected is an error rather
    /// than something to interpret generously.
    /// </summary>
    private ref struct DerReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        public DerReader ReadSequence()
        {
            var content = ReadTagged(0x30, "SEQUENCE");
            return new DerReader(content);
        }

        /// <summary>Reads an INTEGER, returning it without its sign padding or leading zeros.</summary>
        public ReadOnlySpan<byte> ReadInteger()
        {
            var content = ReadTagged(0x02, "INTEGER");
            if (content.IsEmpty)
                throw new CryptographicException("DER INTEGER has no content.");

            var trimmed = content;
            while (trimmed.Length > 1 && trimmed[0] == 0) trimmed = trimmed[1..];
            if ((trimmed[0] & 0x80) != 0 && content[0] != 0)
                throw new CryptographicException("DER INTEGER is negative; an ECDSA signature component cannot be.");
            return trimmed.Length == 1 && trimmed[0] == 0 ? [] : trimmed;
        }

        public void ExpectEnd(string what)
        {
            if (!_data.IsEmpty)
                throw new CryptographicException($"Trailing bytes after the {what}.");
        }

        private ReadOnlySpan<byte> ReadTagged(byte tag, string what)
        {
            if (_data.Length < 2)
                throw new CryptographicException($"DER {what} is truncated.");
            if (_data[0] != tag)
                throw new CryptographicException($"Expected a DER {what} (tag 0x{tag:X2}) but found tag 0x{_data[0]:X2}.");

            var length = _data[1];
            var offset = 2;
            if ((length & 0x80) != 0)
            {
                var lengthBytes = length & 0x7F;
                // Indefinite length is legal BER but not DER, and a length wider
                // than four bytes cannot address anything we would accept anyway.
                if (lengthBytes is 0 or > 4)
                    throw new CryptographicException($"DER {what} has an unsupported length encoding.");
                if (_data.Length < offset + lengthBytes)
                    throw new CryptographicException($"DER {what} is truncated in its length.");

                length = 0;
                var value = 0;
                for (var i = 0; i < lengthBytes; i++) value = (value << 8) | _data[offset + i];
                offset += lengthBytes;
                if (value < 0 || _data.Length < offset + value)
                    throw new CryptographicException($"DER {what} claims more content than the buffer holds.");
                var longContent = _data.Slice(offset, value);
                _data = _data[(offset + value)..];
                return longContent;
            }

            if (_data.Length < offset + length)
                throw new CryptographicException($"DER {what} claims more content than the buffer holds.");
            var content = _data.Slice(offset, length);
            _data = _data[(offset + length)..];
            return content;
        }
    }
}
