using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MeowSSH.Core.Storage;

/// <summary>
/// The length-prefixed encoding every vault record is written in.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than JSON, for two reasons. Secrets would pass through
/// interned base64 strings on the way in and out, and a <see cref="string"/>
/// cannot be zeroed — the plaintext of every private key would sit in the heap
/// until a garbage collection happened to move it. And a serializer that infers
/// its shape from the type would let a field added upstairs silently change the
/// on-disk format of a file that is already encrypted on someone's phone.
/// </para>
/// <para>
/// Everything is big-endian and length-prefixed. Fixed-width fields would be
/// shorter, but a reader that can tell "this file was written by a newer
/// version" from "this file is corrupt" is worth the four bytes.
/// </para>
/// </remarks>
internal ref struct VaultReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;
    private int _position = 0;

    public readonly bool IsAtEnd => _position >= _buffer.Length;

    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));

    public byte ReadByte() => Take(1)[0];

    public bool ReadBoolean() => ReadByte() != 0;

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadInt32();
        if (length < 0) throw new VaultFormatException("A length prefix was negative.");
        return Take(length);
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    public string? ReadNullableString() => ReadBoolean() ? ReadString() : null;

    public Guid ReadGuid() => new(Take(16));

    public Guid? ReadNullableGuid() => ReadBoolean() ? ReadGuid() : null;

    /// <summary>Reads a fixed number of bytes with no length prefix, for the file's magic.</summary>
    public ReadOnlySpan<byte> ReadRaw(int count) => Take(count);

    public byte[]? ReadNullableBytes() => ReadBoolean() ? ReadBytes().ToArray() : null;

    /// <summary>
    /// Reads a timestamp as UTC ticks. Stored as ticks rather than as text
    /// because a round-trip through a string is a chance to lose the offset, and
    /// sync compares these for ordering.
    /// </summary>
    public DateTimeOffset ReadTimestamp() => new(ReadInt64(), TimeSpan.Zero);

    public DateTimeOffset? ReadNullableTimestamp() => ReadBoolean() ? ReadTimestamp() : null;

    public IReadOnlyList<string> ReadStringList()
    {
        var count = ReadInt32();
        if (count < 0) throw new VaultFormatException("A collection length was negative.");
        if (count == 0) return [];
        var items = new string[count];
        for (var i = 0; i < count; i++) items[i] = ReadString();
        return items;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > _buffer.Length - _position)
            throw new VaultFormatException(
                $"The vault ends {count - (_buffer.Length - _position)} bytes early; it is truncated.");
        var slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }
}

/// <summary>Writes the encoding <see cref="VaultReader"/> reads.</summary>
/// <remarks>
/// Grows its own array rather than using a <see cref="MemoryStream"/> so that
/// <see cref="Dispose"/> can zero it. A stream's internal buffer is private and
/// is handed to the garbage collector still holding the plaintext of every
/// private key that was written through it.
/// </remarks>
internal sealed class VaultWriter : IDisposable
{
    private byte[] _buffer = new byte[256];
    private int _length;
    private bool _disposed;

    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(Reserve(4), value);
    }

    public void WriteInt64(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(Reserve(8), value);
    }

    public void WriteByte(byte value) => Reserve(1)[0] = value;

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteInt32(value.Length);
        value.CopyTo(Reserve(value.Length));
    }

    public void WriteString(string value) => WriteBytes(Encoding.UTF8.GetBytes(value));

    public void WriteNullableString(string? value)
    {
        WriteBoolean(value is not null);
        if (value is not null) WriteString(value);
    }

    public void WriteGuid(Guid value) => value.TryWriteBytes(Reserve(16));

    public void WriteNullableGuid(Guid? value)
    {
        WriteBoolean(value.HasValue);
        if (value.HasValue) WriteGuid(value.Value);
    }

    public void WriteNullableBytes(byte[]? value)
    {
        WriteBoolean(value is not null);
        if (value is not null) WriteBytes(value);
    }

    public void WriteTimestamp(DateTimeOffset value) => WriteInt64(value.UtcTicks);

    public void WriteNullableTimestamp(DateTimeOffset? value)
    {
        WriteBoolean(value.HasValue);
        if (value.HasValue) WriteTimestamp(value.Value);
    }

    public void WriteStringList(IReadOnlyList<string> values)
    {
        WriteInt32(values.Count);
        foreach (var value in values) WriteString(value);
    }

    /// <summary>Appends bytes with no length prefix, for the file's magic.</summary>
    public void WriteRaw(ReadOnlySpan<byte> value) => value.CopyTo(Reserve(value.Length));

    public byte[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _buffer.AsSpan(0, _length).ToArray();
    }

    private Span<byte> Reserve(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_length + count > _buffer.Length) Grow(_length + count);
        var slice = _buffer.AsSpan(_length, count);
        _length += count;
        return slice;
    }

    private void Grow(int required)
    {
        var capacity = _buffer.Length;
        while (capacity < required) capacity *= 2;

        var replacement = new byte[capacity];
        _buffer.AsSpan(0, _length).CopyTo(replacement);
        // The old array still holds everything written so far, so it is wiped
        // rather than abandoned -- otherwise growing the buffer would scatter
        // copies of the vault's plaintext across the heap.
        CryptographicOperations.ZeroMemory(_buffer);
        _buffer = replacement;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_buffer);
    }
}

/// <summary>The vault on disk is not something this version can read.</summary>
public sealed class VaultFormatException(string message) : Exception(message);
