using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class TransferHistoryServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("meowssh-transfer-history-").FullName;
    private string PathTo => Path.Combine(_directory, "transfer-history.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task RecordedEntriesSurviveAFreshInstanceReadingTheSameFile()
    {
        var first = new FileTransferHistoryService(PathTo);
        var entry = Entry(state: TransferItemState.Completed, endedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        await first.RecordAsync(entry);

        var second = new FileTransferHistoryService(PathTo);
        var all = await second.GetAllAsync();
        var stored = Assert.Single(all);
        Assert.Equal(entry.Id, stored.Id);
        Assert.Equal(entry.TransferId, stored.TransferId);
        Assert.Equal(entry.HostLabel, stored.HostLabel);
        Assert.Equal(TransferItemState.Completed, stored.State);
    }

    [Fact]
    public async Task RetryOfTheSameTransferAppendsRatherThanOverwritingTheEarlierAttempt()
    {
        var service = new FileTransferHistoryService(PathTo);
        var transferId = Guid.NewGuid();
        await service.RecordAsync(Entry(transferId: transferId, attempt: 1, state: TransferItemState.Failed,
            endedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
        await service.RecordAsync(Entry(transferId: transferId, attempt: 2, state: TransferItemState.Completed,
            endedAt: DateTimeOffset.Parse("2026-01-01T00:05:00Z")));

        var all = await service.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.TransferId == transferId && e.Attempt == 1 && e.State == TransferItemState.Failed);
        Assert.Contains(all, e => e.TransferId == transferId && e.Attempt == 2 && e.State == TransferItemState.Completed);
    }

    [Fact]
    public async Task GetAllOrdersNewestAttemptFirst()
    {
        var service = new FileTransferHistoryService(PathTo);
        await service.RecordAsync(Entry(endedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"), hostLabel: "oldest"));
        await service.RecordAsync(Entry(endedAt: DateTimeOffset.Parse("2026-01-03T00:00:00Z"), hostLabel: "newest"));
        await service.RecordAsync(Entry(endedAt: DateTimeOffset.Parse("2026-01-02T00:00:00Z"), hostLabel: "middle"));

        var all = await service.GetAllAsync();

        Assert.Equal(["newest", "middle", "oldest"], all.Select(e => e.HostLabel));
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheNamedEntry()
    {
        var service = new FileTransferHistoryService(PathTo);
        var keep = Entry(hostLabel: "keep");
        var drop = Entry(hostLabel: "drop");
        await service.RecordAsync(keep);
        await service.RecordAsync(drop);

        await service.DeleteAsync(drop.Id);

        var remaining = Assert.Single(await service.GetAllAsync());
        Assert.Equal("keep", remaining.HostLabel);
    }

    [Fact]
    public async Task ClearRemovesEveryEntry()
    {
        var service = new FileTransferHistoryService(PathTo);
        await service.RecordAsync(Entry());
        await service.RecordAsync(Entry());

        await service.ClearAsync();

        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task RetentionKeepsOnlyTheNewestEntriesOnceTheCapIsExceeded()
    {
        var service = new FileTransferHistoryService(PathTo);
        const int cap = 500;
        for (var i = 0; i < cap + 10; i++)
        {
            await service.RecordAsync(Entry(
                hostLabel: $"host-{i}",
                endedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(i)));
        }

        var all = await service.GetAllAsync();

        Assert.Equal(cap, all.Count);
        // The 10 oldest attempts (host-0..host-9) were trimmed; the most recent survive.
        Assert.DoesNotContain(all, e => e.HostLabel is "host-0" or "host-9");
        Assert.Contains(all, e => e.HostLabel == $"host-{cap + 9}");
    }

    [Fact]
    public async Task ChangedFiresOnRecordDeleteAndClear()
    {
        var service = new FileTransferHistoryService(PathTo);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var entry = Entry();
        await service.RecordAsync(entry);
        await service.DeleteAsync(entry.Id);
        await service.RecordAsync(Entry());
        await service.ClearAsync();

        Assert.Equal(4, changes);
    }

    [Fact]
    public void AWhitespacePathIsRejectedUpFront()
    {
        Assert.Throws<ArgumentException>(() => new FileTransferHistoryService("  "));
    }

    private static TransferHistoryEntry Entry(
        Guid? transferId = null,
        string hostLabel = "host",
        TransferItemState state = TransferItemState.Completed,
        int attempt = 1,
        DateTimeOffset? endedAt = null) => new(
            Guid.NewGuid(),
            transferId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            hostLabel,
            TransferDirection.Upload,
            "/local/file",
            "/remote/file",
            TotalBytes: 100,
            TransferredBytes: 100,
            state,
            Error: state == TransferItemState.Failed ? "boom" : null,
            attempt,
            EnqueuedAtUtc: (endedAt ?? DateTimeOffset.UtcNow).AddSeconds(-5),
            StartedAtUtc: (endedAt ?? DateTimeOffset.UtcNow).AddSeconds(-3),
            EndedAtUtc: endedAt ?? DateTimeOffset.UtcNow);
}
