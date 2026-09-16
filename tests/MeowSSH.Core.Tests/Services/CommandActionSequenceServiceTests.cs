using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class CommandActionSequenceServiceTests
{
    [Fact]
    public async Task FreeTierCannotReadOrSaveSequenceDefinitions()
    {
        var actionStore = new MemoryCommandActionStore();
        var action = new CommandAction(Guid.NewGuid(), "Deploy", "echo start", [Guid.NewGuid()]);
        await actionStore.SaveAsync(action);
        var sequenceStore = new MemoryCommandActionSequenceStore();
        var service = Service(sequenceStore, actionStore, pro: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync(action.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(new CommandActionSequence(action.Id, ["echo finish"])));
        Assert.Null(await sequenceStore.GetAsync(action.Id));
    }

    [Fact]
    public async Task ProSequenceIsNormalizedAndBounded()
    {
        var actionStore = new MemoryCommandActionStore();
        var action = new CommandAction(Guid.NewGuid(), "Deploy", "echo start", [Guid.NewGuid()]);
        await actionStore.SaveAsync(action);
        var sequenceStore = new MemoryCommandActionSequenceStore();
        var service = Service(sequenceStore, actionStore, pro: true);

        await service.SaveAsync(new CommandActionSequence(action.Id, [" echo middle ", " echo finish "], false));

        var saved = Assert.IsType<CommandActionSequence>(await service.GetAsync(action.Id));
        Assert.Equal(["echo middle", "echo finish"], saved.AdditionalCommands);
        Assert.False(saved.StopOnError);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(
            new CommandActionSequence(action.Id, Enumerable.Range(0, 10).Select(i => $"echo {i}").ToArray())));
    }

    [Fact]
    public async Task SequenceCannotBeAttachedToDeletedAction()
    {
        var actionStore = new MemoryCommandActionStore();
        var service = Service(new MemoryCommandActionSequenceStore(), actionStore, pro: true);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.SaveAsync(new CommandActionSequence(Guid.NewGuid(), ["echo orphan"])));
    }

    private static CommandActionSequenceService Service(
        ICommandActionSequenceStore sequences,
        ICommandActionStore actions,
        bool pro) =>
        new(sequences, actions, null!, null!, null!, null!, new FakeEntitlements(pro));

    private sealed class FakeEntitlements(bool pro) : IEntitlementService
    {
        public EntitlementSnapshot Current { get; } = pro
            ? new(EntitlementTier.Pro, EntitlementSource.Promotional, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1))
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);
        public event EventHandler? Changed { add { } remove { } }
        public bool Has(PremiumFeature feature) => pro;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }
}
