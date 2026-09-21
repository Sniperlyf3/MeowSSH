using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Services;

public sealed class TransferQueueServiceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly List<TransferQueueService> _services = [];

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
    }

    private TransferQueueService NewService(IEntitlementService entitlements, ITransferHistoryService? history = null, TimeProvider? timeProvider = null)
    {
        var service = new TransferQueueService(entitlements, history ?? new RecordingHistory(), timeProvider);
        _services.Add(service);
        return service;
    }

    [Fact]
    public async Task FreeTierCannotEnqueueOrRetry()
    {
        var service = NewService(new FakeEntitlements(pro: false));
        var sftp = new ImmediateSftp();

        var enqueueError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueueAsync(sftp, Guid.NewGuid(), "host", TransferDirection.Upload, "/local", "/remote", 10));
        Assert.Contains("Pro", enqueueError.Message, StringComparison.Ordinal);
        Assert.Empty(service.Items);

        // A retry is also gated -- a lapsed license should not let a free user
        // re-run a transfer they could not have queued in the first place.
        var proService = NewService(new FakeEntitlements(pro: true));
        var item = await proService.EnqueueAsync(sftp, Guid.NewGuid(), "host", TransferDirection.Upload, "/local", "/remote", 10);
        await WaitForStateAsync(proService, item.Id, TransferItemState.Completed);

        var lapsedError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RetryAsync(item.Id, sftp));
        Assert.Contains("Pro", lapsedError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueuedTransfersRunOneAtATimeInEnqueueOrder()
    {
        var service = NewService(new FakeEntitlements(pro: true));
        var first = new GatedSftp();
        var second = new GatedSftp();

        var a = await service.EnqueueAsync(first, Guid.NewGuid(), "host-a", TransferDirection.Upload, "/a", "/a", 10);
        var b = await service.EnqueueAsync(second, Guid.NewGuid(), "host-b", TransferDirection.Upload, "/b", "/b", 10);

        // Both are queued, but only the first has actually been handed to its session.
        await first.Started.WaitAsync(Timeout);
        Assert.Equal(TransferItemState.InProgress, service.Items.Single(i => i.Id == a.Id).State);
        Assert.Equal(TransferItemState.Queued, service.Items.Single(i => i.Id == b.Id).State);
        Assert.Equal(0, second.CallCount);

        first.Release();
        await second.Started.WaitAsync(Timeout);
        Assert.Equal(TransferItemState.InProgress, service.Items.Single(i => i.Id == b.Id).State);

        second.Release();
        await WaitForStateAsync(service, b.Id, TransferItemState.Completed);
        Assert.Equal(TransferItemState.Completed, service.Items.Single(i => i.Id == a.Id).State);
    }

    [Fact]
    public async Task ProgressReportsFlowThroughToTheItemSnapshot()
    {
        var service = NewService(new FakeEntitlements(pro: true));
        var sftp = new GatedSftp();
        var item = await service.EnqueueAsync(sftp, Guid.NewGuid(), "host", TransferDirection.Upload, "/local", "/remote", totalBytes: null);

        await AssertFirstReportIsZeroAsync(service, item.Id);
        sftp.Release();
        var finished = await WaitForStateAsync(service, item.Id, TransferItemState.Completed);

        Assert.Equal(100, finished.TransferredBytes);
        Assert.Equal(100, finished.TotalBytes);
        Assert.Equal(1d, finished.Fraction);

        static async Task AssertFirstReportIsZeroAsync(TransferQueueService queue, Guid id)
        {
            var snapshot = await WaitForAsync(queue, () =>
            {
                var current = queue.Items.FirstOrDefault(i => i.Id == id);
                return current is { State: TransferItemState.InProgress, TotalBytes: 100 };
            }, Timeout);
            Assert.Equal(0, snapshot.TransferredBytes);
        }
    }

    [Fact]
    public async Task CancellingAQueuedItemFinishesItImmediatelyWithoutTouchingItsSession()
    {
        var service = NewService(new FakeEntitlements(pro: true));
        var running = new GatedSftp();
        var queued = new GatedSftp();
        var a = await service.EnqueueAsync(running, Guid.NewGuid(), "host-a", TransferDirection.Upload, "/a", "/a", 10);
        var b = await service.EnqueueAsync(queued, Guid.NewGuid(), "host-b", TransferDirection.Upload, "/b", "/b", 10);
        await running.Started.WaitAsync(Timeout);

        await service.CancelAsync(b.Id);

        var cancelled = service.Items.Single(i => i.Id == b.Id);
        Assert.Equal(TransferItemState.Cancelled, cancelled.State);
        Assert.Equal(0, queued.CallCount);
        Assert.NotNull(cancelled.EndedAtUtc);

        running.Release();
        await WaitForStateAsync(service, a.Id, TransferItemState.Completed);
    }

    [Fact]
    public async Task CancellingARunningItemUnwindsToCancelledAndFreesTheWorkerForTheNextItem()
    {
        var service = NewService(new FakeEntitlements(pro: true));
        var first = new GatedSftp();
        var second = new GatedSftp();
        var a = await service.EnqueueAsync(first, Guid.NewGuid(), "host-a", TransferDirection.Upload, "/a", "/a", 10);
        var b = await service.EnqueueAsync(second, Guid.NewGuid(), "host-b", TransferDirection.Upload, "/b", "/b", 10);
        await first.Started.WaitAsync(Timeout);

        await service.CancelAsync(a.Id);
        // Still InProgress until the in-flight upload actually observes the cancellation.
        Assert.Equal(TransferItemState.InProgress, service.Items.Single(i => i.Id == a.Id).State);

        var finished = await WaitForStateAsync(service, a.Id, TransferItemState.Cancelled);
        Assert.Null(finished.Error);

        await second.Started.WaitAsync(Timeout);
        second.Release();
        await WaitForStateAsync(service, b.Id, TransferItemState.Completed);
    }

    [Fact]
    public async Task CancellingAFinishedItemIsANoOp()
    {
        var service = NewService(new FakeEntitlements(pro: true));
        var sftp = new ImmediateSftp();
        var item = await service.EnqueueAsync(sftp, Guid.NewGuid(), "host", TransferDirection.Upload, "/a", "/a", 10);
        await WaitForStateAsync(service, item.Id, TransferItemState.Completed);

        await service.CancelAsync(item.Id);

        Assert.Equal(TransferItemState.Completed, service.Items.Single(i => i.Id == item.Id).State);
    }

    [Fact]
    public async Task FailedTransferDoesNotBlockLaterQueuedItems()
    {
        var history = new RecordingHistory();
        var service = NewService(new FakeEntitlements(pro: true), history);
        var failing = new ImmediateSftp(failWith: new IOException("disk full"));
        var succeeding = new ImmediateSftp();

        var a = await service.EnqueueAsync(failing, Guid.NewGuid(), "host-a", TransferDirection.Upload, "/a", "/a", 10);
        var b = await service.EnqueueAsync(succeeding, Guid.NewGuid(), "host-b", TransferDirection.Upload, "/b", "/b", 10);

        var failed = await WaitForStateAsync(service, a.Id, TransferItemState.Failed);
        Assert.Equal("disk full", failed.Error);

        var completed = await WaitForStateAsync(service, b.Id, TransferItemState.Completed);
        Assert.Null(completed.Error);

        Assert.Equal(2, history.Entries.Count);
        Assert.Contains(history.Entries, e => e.TransferId == a.Id && e.State == TransferItemState.Failed);
        Assert.Contains(history.Entries, e => e.TransferId == b.Id && e.State == TransferItemState.Completed);
    }

    [Fact]
    public async Task RetryReusesTheItemWithAFreshSessionAndIncrementsAttempt()
    {
        var service = NewService(new FakeEntitlements(pro: true));
        var failing = new ImmediateSftp(failWith: new IOException("connection reset"));
        var item = await service.EnqueueAsync(failing, Guid.NewGuid(), "host", TransferDirection.Upload, "/a", "/a", 10);
        var failed = await WaitForStateAsync(service, item.Id, TransferItemState.Failed);
        Assert.Equal(1, failed.Attempt);

        var retrying = new ImmediateSftp();
        var retried = await service.RetryAsync(item.Id, retrying);
        Assert.Equal(2, retried.Attempt);
        Assert.Equal(TransferItemState.Queued, retried.State);

        var succeeded = await WaitForStateAsync(service, item.Id, TransferItemState.Completed);
        Assert.Equal(2, succeeded.Attempt);
        Assert.Null(succeeded.Error);
        Assert.Equal(1, failing.CallCount); // the original, now-abandoned session was never reused
        Assert.Equal(1, retrying.CallCount);
    }

    [Fact]
    public async Task OnlyAFailedOrCancelledItemCanBeRetried()
    {
        var service = NewService(new FakeEntitlements(pro: true));
        var sftp = new ImmediateSftp();
        var item = await service.EnqueueAsync(sftp, Guid.NewGuid(), "host", TransferDirection.Upload, "/a", "/a", 10);
        await WaitForStateAsync(service, item.Id, TransferItemState.Completed);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryAsync(item.Id, sftp));
        Assert.Contains("failed or cancelled", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DismissRemovesOnlyFinishedItemsFromTheVisibleQueue()
    {
        var service = NewService(new FakeEntitlements(pro: true));
        var running = new GatedSftp();
        var a = await service.EnqueueAsync(running, Guid.NewGuid(), "host-a", TransferDirection.Upload, "/a", "/a", 10);
        await running.Started.WaitAsync(Timeout);

        // Dismissing a running item is a no-op: history has no record of it yet.
        await service.DismissAsync(a.Id);
        Assert.Single(service.Items);

        running.Release();
        await WaitForStateAsync(service, a.Id, TransferItemState.Completed);
        await service.DismissAsync(a.Id);
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task ClearFinishedLeavesQueuedAndRunningItemsAlone()
    {
        var service = NewService(new FakeEntitlements(pro: true));
        var done = new ImmediateSftp();
        var running = new GatedSftp();
        var finished = await service.EnqueueAsync(done, Guid.NewGuid(), "host-a", TransferDirection.Upload, "/a", "/a", 10);
        await WaitForStateAsync(service, finished.Id, TransferItemState.Completed);
        var active = await service.EnqueueAsync(running, Guid.NewGuid(), "host-b", TransferDirection.Upload, "/b", "/b", 10);
        await running.Started.WaitAsync(Timeout);

        await service.ClearFinishedAsync();

        var remaining = Assert.Single(service.Items);
        Assert.Equal(active.Id, remaining.Id);

        running.Release();
        await WaitForStateAsync(service, active.Id, TransferItemState.Completed);
    }

    private static async Task<TransferQueueItem> WaitForStateAsync(
        TransferQueueService queue, Guid id, TransferItemState state, TimeSpan? timeout = null) =>
        await WaitForAsync(queue, () =>
        {
            var current = queue.Items.FirstOrDefault(item => item.Id == id);
            return current is not null && current.State == state;
        }, timeout ?? Timeout, () => queue.Items.First(item => item.Id == id));

    private static Task<TransferQueueItem> WaitForAsync(
        TransferQueueService queue, Func<bool> predicate, TimeSpan timeout) =>
        WaitForAsync(queue, predicate, timeout, () => queue.Items.First(item => predicate()));

    private static async Task<TransferQueueItem> WaitForAsync(
        TransferQueueService queue, Func<bool> predicate, TimeSpan timeout, Func<TransferQueueItem> select)
    {
        if (predicate()) return select();

        var tcs = new TaskCompletionSource<TransferQueueItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (predicate()) tcs.TrySetResult(select());
        }

        queue.Changed += OnChanged;
        try
        {
            if (predicate()) return select();
            using var cts = new CancellationTokenSource(timeout);
            using var registration = cts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException("Timed out waiting for the transfer queue to reach the expected state.")));
            return await tcs.Task;
        }
        finally
        {
            queue.Changed -= OnChanged;
        }
    }

    private sealed class FakeEntitlements(bool pro) : IEntitlementService
    {
        public EntitlementSnapshot Current => pro
            ? new EntitlementSnapshot(EntitlementTier.Pro, EntitlementSource.Promotional, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1))
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Has(PremiumFeature feature) => pro;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }

    private sealed class RecordingHistory : ITransferHistoryService
    {
        public List<TransferHistoryEntry> Entries { get; } = [];

        public event EventHandler? Changed;

        public Task<IReadOnlyList<TransferHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransferHistoryEntry>>([.. Entries]);

        public Task RecordAsync(TransferHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Entries.Clear();
            return Task.CompletedTask;
        }
    }

    /// <summary>An <see cref="ISftpSession"/> whose transfer completes or fails as soon as it is called.</summary>
    private sealed class ImmediateSftp(Exception? failWith = null) : ISftpSession
    {
        public int CallCount { get; private set; }

        public Task UploadAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
            RunAsync(progress, cancellationToken);

        public Task DownloadAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
            RunAsync(progress, cancellationToken);

        private Task RunAsync(IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            progress?.Report(new TransferProgress(10, 10));
            if (failWith is not null) throw failWith;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteFile> StatAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(RemoteFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// An <see cref="ISftpSession"/> whose transfer blocks after reporting initial progress until
    /// the test calls <see cref="Release"/> -- the only way to observe an item actually sitting in
    /// <see cref="TransferItemState.InProgress"/>, rather than assuming the pump got there.
    /// </summary>
    private sealed class GatedSftp : ISftpSession
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public int CallCount { get; private set; }

        public void Release() => _release.TrySetResult();

        public Task UploadAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
            RunAsync(progress, cancellationToken);

        public Task DownloadAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
            RunAsync(progress, cancellationToken);

        private async Task RunAsync(IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
        {
            CallCount++;
            progress?.Report(new TransferProgress(0, 100));
            _started.TrySetResult();
            using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), _release);
            await _release.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new TransferProgress(100, 100));
        }

        public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteFile> StatAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(RemoteFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
