using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MeowSSH.Core.Security;

/// <summary>
/// A byte buffer holding key material, pinned so the garbage collector cannot
/// copy it to a new address and leave the old bytes readable in the heap, and
/// zeroed the moment it is disposed.
/// </summary>
/// <remarks>
/// Never put a secret in a <see cref="string"/>. .NET strings are immutable and
/// relocatable: there is no supported way to overwrite one, and a passphrase
/// that reaches a string survives in the heap until the process exits. Every
/// secret in this codebase travels as a <see cref="SecretBuffer"/> or a
/// <c>ReadOnlySpan&lt;byte&gt;</c> instead.
/// </remarks>
public sealed class SecretBuffer : IDisposable
{
    private readonly byte[] _bytes;
    private GCHandle _pin;
    private bool _disposed;

    private SecretBuffer(byte[] bytes)
    {
        _bytes = bytes;
        _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
    }

    /// <summary>
    /// Allocates a zero-filled buffer of <paramref name="length"/> bytes.
    /// </summary>
    /// <remarks>
    /// Zero length is allowed: a stored secret can legitimately be empty (a blank
    /// passphrase field), and decryption has to be able to return that without
    /// special-casing it. A zero-length <em>key</em> is always a bug, which is why
    /// <see cref="Random"/> refuses one.
    /// </remarks>
    public static SecretBuffer Allocate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new SecretBuffer(new byte[length]);
    }

    /// <summary>Allocates a buffer filled with cryptographically secure random bytes.</summary>
    public static SecretBuffer Random(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var buffer = Allocate(length);
        RandomNumberGenerator.Fill(buffer._bytes);
        return buffer;
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a new pinned buffer. The caller still
    /// owns <paramref name="source"/> and is responsible for clearing it.
    /// </summary>
    public static SecretBuffer CopyFrom(ReadOnlySpan<byte> source)
    {
        var buffer = Allocate(source.Length);
        source.CopyTo(buffer._bytes);
        return buffer;
    }

    /// <summary>
    /// Takes ownership of <paramref name="array"/> without copying it.
    /// </summary>
    /// <remarks>
    /// The array was allocated unpinned, so the GC may already have copied it;
    /// those copies cannot be reached to clear them. Prefer <see cref="Allocate"/>
    /// or <see cref="Random"/>, and use this only at a boundary that hands you an
    /// array you cannot avoid (a decoder's output, say).
    /// </remarks>
    public static SecretBuffer TakeOwnershipOf(byte[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        return new SecretBuffer(array);
    }

    public int Length => _bytes.Length;

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes;
        }
    }

    public ReadOnlySpan<byte> ReadOnlySpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes;
        }
    }

    /// <summary>Zeroes the buffer in place without releasing it.</summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CryptographicOperations.ZeroMemory(_bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_bytes);
        if (_pin.IsAllocated) _pin.Free();
    }
}
