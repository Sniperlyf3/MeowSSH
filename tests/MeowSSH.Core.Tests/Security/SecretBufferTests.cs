using MeowSSH.Core.Security;

namespace MeowSSH.Core.Tests.Security;

public class SecretBufferTests
{
    [Fact]
    public void AllocateStartsZeroed()
    {
        using var buffer = SecretBuffer.Allocate(32);
        Assert.All(buffer.ReadOnlySpan.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void RandomProducesDifferentBytesEachTime()
    {
        using var first = SecretBuffer.Random(32);
        using var second = SecretBuffer.Random(32);
        Assert.NotEqual(first.ReadOnlySpan.ToArray(), second.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void CopyFromDoesNotAliasTheSource()
    {
        var source = new byte[] { 1, 2, 3, 4 };
        using var buffer = SecretBuffer.CopyFrom(source);

        source[0] = 99;

        Assert.Equal(1, buffer.ReadOnlySpan[0]);
    }

    [Fact]
    public void ClearZeroesInPlaceButKeepsTheBufferUsable()
    {
        using var buffer = SecretBuffer.Random(16);
        buffer.Clear();

        Assert.All(buffer.ReadOnlySpan.ToArray(), b => Assert.Equal(0, b));
        buffer.Span[0] = 7;
        Assert.Equal(7, buffer.ReadOnlySpan[0]);
    }

    [Fact]
    public void DisposeZeroesTheUnderlyingArray()
    {
        // Reach the same array the buffer wrapped, so we can prove disposal
        // actually overwrote the secret rather than merely dropping the reference.
        var backing = new byte[] { 9, 9, 9, 9 };
        var buffer = SecretBuffer.TakeOwnershipOf(backing);

        buffer.Dispose();

        Assert.All(backing, b => Assert.Equal(0, b));
    }

    [Fact]
    public void UseAfterDisposeThrowsRatherThanReadingFreedMemory()
    {
        var buffer = SecretBuffer.Allocate(8);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.ReadOnlySpan.ToArray());
        Assert.Throws<ObjectDisposedException>(() => buffer.Clear());
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var buffer = SecretBuffer.Allocate(8);
        buffer.Dispose();
        buffer.Dispose();
    }

    [Fact]
    public void ZeroLengthAllocationIsAllowedBecauseAStoredSecretCanBeEmpty()
    {
        using var buffer = SecretBuffer.Allocate(0);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void ZeroLengthRandomKeyIsRejectedBecauseThatIsAlwaysABug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SecretBuffer.Random(0));
    }
}
