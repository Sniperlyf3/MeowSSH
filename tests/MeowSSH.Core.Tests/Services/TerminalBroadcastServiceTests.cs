using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Services;

public sealed class TerminalBroadcastServiceTests
{
    [Fact]
    public async Task FreeTierFailsBeforeAnyTerminalWrite()
    {
        var first = new FakeTerminal();
        var second = new FakeTerminal();
        var service = new TerminalBroadcastService(new FakeEntitlements(false));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(
            [new(Guid.NewGuid(), first), new(Guid.NewGuid(), second)],
            new byte[] { (byte)'x' }));

        Assert.Contains("Pro", error.Message, StringComparison.Ordinal);
        Assert.Empty(first.Writes);
        Assert.Empty(second.Writes);
    }

    [Fact]
    public async Task ProBroadcastWritesIdenticalBytesToEveryTarget()
    {
        var first = new FakeTerminal();
        var second = new FakeTerminal();
        var third = new FakeTerminal();
        var service = new TerminalBroadcastService(new FakeEntitlements(true));
        var bytes = new byte[] { (byte)'p', (byte)'w', (byte)'d', 0x0d };

        var result = await service.WriteAsync(
            [new(Guid.NewGuid(), first), new(Guid.NewGuid(), second), new(Guid.NewGuid(), third)], bytes);

        Assert.True(result.SucceededCompletely);
        Assert.Equal(bytes, Assert.Single(first.Writes));
        Assert.Equal(bytes, Assert.Single(second.Writes));
        Assert.Equal(bytes, Assert.Single(third.Writes));
    }

    [Fact]
    public async Task FailureOnOneTargetDoesNotPreventOtherTargetsReceivingInput()
    {
        var first = new FakeTerminal();
        var broken = new FakeTerminal(fail: true);
        var third = new FakeTerminal();
        var brokenId = Guid.NewGuid();
        var service = new TerminalBroadcastService(new FakeEntitlements(true));

        var result = await service.WriteAsync(
            [new(Guid.NewGuid(), first), new(brokenId, broken), new(Guid.NewGuid(), third)],
            new byte[] { (byte)'x' });

        Assert.Equal(3, result.Attempted);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(brokenId, Assert.Single(result.Failures).Id);
        Assert.Single(first.Writes);
        Assert.Single(third.Writes);
    }

    private sealed class FakeTerminal(bool fail = false) : ITerminalSession
    {
        public List<byte[]> Writes { get; } = [];
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (fail) throw new IOException("write failed");
            Writes.Add(data.ToArray());
            return Task.CompletedTask;
        }
        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEntitlements(bool pro) : IEntitlementService
    {
        public EntitlementSnapshot Current { get; } = pro
            ? new(EntitlementTier.Pro, EntitlementSource.Promotional, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1))
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);
        public event EventHandler? Changed { add { } remove { } }
        public bool Has(PremiumFeature feature) => pro && feature == PremiumFeature.BroadcastInput;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }
}
