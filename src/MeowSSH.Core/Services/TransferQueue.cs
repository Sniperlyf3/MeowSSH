using MeowSSH.Core.Licensing;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

public enum TransferDirection
{
    Upload,
    Download,
}

public enum TransferItemState
{
    Queued,
    InProgress,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A live snapshot of one queued or running transfer.</summary>
public sealed record TransferQueueItem(
    Guid Id,
    Guid HostId,
    string HostLabel,
    TransferDirection Direction,
    string LocalPath,
    string RemotePath,
    long? TotalBytes,
    TransferItemState State,
    long TransferredBytes,
    int Attempt,
    string? Error,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc)
{
    public bool IsFinished => State is TransferItemState.Completed or TransferItemState.Failed or TransferItemState.Cancelled;

    public double? Fraction => TotalBytes is > 0
        ? Math.Clamp((double)TransferredBytes / TotalBytes.Value, 0d, 1d)
        : null;
}

/// <summary>
/// A completed attempt, recorded once a queued transfer leaves the live queue for good.
/// Every attempt gets its own entry -- a retried transfer is not overwritten, it is appended
/// again with a higher <see cref="Attempt"/>, so a disputed "did it actually finish" question
/// has an audit trail rather than a single mutable row.
/// </summary>
public sealed record TransferHistoryEntry(
    Guid Id,
    Guid TransferId,
    Guid HostId,
    string HostLabel,
    TransferDirection Direction,
    string LocalPath,
    string RemotePath,
    long? TotalBytes,
    long TransferredBytes,
    TransferItemState State,
    string? Error,
    int Attempt,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset EndedAtUtc);

public interface ITransferHistoryService
{
    event EventHandler? Changed;

    /// <summary>
    /// History survives an entitlement lapse, same as session logs: it is the user's own
    /// record of what already happened, not a feature to be revoked retroactively.
    /// </summary>
    Task<IReadOnlyList<TransferHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    Task RecordAsync(TransferHistoryEntry entry, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface ITransferQueueService
{
    event EventHandler? Changed;

    /// <summary>A point-in-time snapshot of every queued, running or not-yet-dismissed item.</summary>
    IReadOnlyList<TransferQueueItem> Items { get; }

    /// <summary>
    /// Appends a transfer to the end of the queue. Requires MeowSSH Pro -- free users keep the
    /// existing fire-and-forget single transfer in <c>FilesPage</c>, which this does not replace.
    /// </summary>
    Task<TransferQueueItem> EnqueueAsync(
        ISftpSession sftp,
        Guid hostId,
        string hostLabel,
        TransferDirection direction,
        string localPath,
        string remotePath,
        long? totalBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a queued item outright, or requests cancellation of the one currently running.
    /// A no-op for an item that has already finished.
    /// </summary>
    Task CancelAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-queues a failed or cancelled item, against a caller-supplied session -- the original
    /// one may belong to a page the user has since navigated away from and closed.
    /// </summary>
    Task<TransferQueueItem> RetryAsync(Guid id, ISftpSession sftp, CancellationToken cancellationToken = default);

    /// <summary>Removes one finished item from the visible queue. History already has its record.</summary>
    Task DismissAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Removes every finished item from the visible queue, leaving queued/running ones alone.</summary>
    Task ClearFinishedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Sequential Pro transfer queue: one transfer runs at a time, in the order it was added, with
/// per-item progress/state and a local, append-only history of every attempt.
/// </summary>
/// <remarks>
/// Sequential by design, not as a current limitation -- running several SFTP transfers at once
/// over one SSH channel is exactly the kind of "fire-and-forget, who knows what's happening"
/// behavior this feature replaces. One worker loop is the whole synchronization story: every
/// mutation happens either under <see cref="_gate"/> (queued/cancelled/retried, all synchronous)
/// or inside the loop itself while a transfer is not running, so two transfers are never being
/// started at once and a snapshot is never read mid-mutation.
/// </remarks>
public sealed class TransferQueueService(
    IEntitlementService entitlements,
    ITransferHistoryService history,
    TimeProvider? timeProvider = null) : ITransferQueueService, IDisposable
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly List<QueuedTransfer> _items = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;
    private bool _disposed;

    public event EventHandler? Changed;

    public IReadOnlyList<TransferQueueItem> Items
    {
        get { lock (_gate) return [.. _items.Select(static item => item.ToSnapshot())]; }
    }

    public Task<TransferQueueItem> EnqueueAsync(
        ISftpSession sftp,
        Guid hostId,
        string hostLabel,
        TransferDirection direction,
        string localPath,
        string remotePath,
        long? totalBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sftp);
        if (hostId == Guid.Empty) throw new ArgumentException("A host ID is required.", nameof(hostId));
        ArgumentException.ThrowIfNullOrWhiteSpace(hostLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        RequirePro();

        var item = new QueuedTransfer(Guid.NewGuid(), sftp, hostId, hostLabel, direction, localPath, remotePath, totalBytes, _timeProvider.GetUtcNow());
        lock (_gate)
        {
            _items.Add(item);
            EnsurePumpStartedUnsafe();
        }
        _signal.Release();
        RaiseChanged();
        return Task.FromResult(item.ToSnapshot());
    }

    public async Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        QueuedTransfer? cancelledInPlace = null;
        lock (_gate)
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null) return;

            if (item.State == TransferItemState.Queued)
            {
                item.State = TransferItemState.Cancelled;
                item.EndedAtUtc = _timeProvider.GetUtcNow();
                cancelledInPlace = item;
            }
            else if (item.State == TransferItemState.InProgress)
            {
                // The pump loop observes this and finalizes the item to Cancelled itself,
                // once the in-flight upload/download actually unwinds.
                item.Cancellation?.Cancel();
            }
        }

        if (cancelledInPlace is not null)
        {
            await history.RecordAsync(cancelledInPlace.ToHistoryEntry(), cancellationToken).ConfigureAwait(false);
            RaiseChanged();
        }
    }

    public Task<TransferQueueItem> RetryAsync(Guid id, ISftpSession sftp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sftp);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        RequirePro();

        QueuedTransfer item;
        lock (_gate)
        {
            item = _items.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new KeyNotFoundException("The transfer no longer exists.");
            if (item.State is not (TransferItemState.Failed or TransferItemState.Cancelled))
                throw new InvalidOperationException("Only a failed or cancelled transfer can be retried.");

            item.Sftp = sftp;
            item.State = TransferItemState.Queued;
            item.Attempt++;
            item.Error = null;
            item.TransferredBytes = 0;
            item.StartedAtUtc = null;
            item.EndedAtUtc = null;
            item.Cancellation = null;
            EnsurePumpStartedUnsafe();
        }
        _signal.Release();
        RaiseChanged();
        return Task.FromResult(item.ToSnapshot());
    }

    public Task DismissAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null || !item.ToSnapshot().IsFinished) return Task.CompletedTask;
            _items.Remove(item);
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task ClearFinishedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _items.RemoveAll(item => item.ToSnapshot().IsFinished);
        RaiseChanged();
        return Task.CompletedTask;
    }

    private void RequirePro()
    {
        if (!entitlements.Has(PremiumFeature.TransferQueue))
            throw new InvalidOperationException("Queuing transfers requires MeowSSH Pro.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void EnsurePumpStartedUnsafe()
    {
        _pump ??= Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Checked every iteration, not just at the top of the outer loop -- otherwise
            // Dispose() mid-drain would not take effect until the whole remaining backlog
            // of already-queued transfers had run, which defeats the point of disposing.
            while (!_shutdown.IsCancellationRequested)
            {
                QueuedTransfer? next;
                lock (_gate)
                {
                    next = _items.FirstOrDefault(item => item.State == TransferItemState.Queued);
                    if (next is not null)
                    {
                        next.State = TransferItemState.InProgress;
                        next.StartedAtUtc = _timeProvider.GetUtcNow();
                        next.Cancellation = new CancellationTokenSource();
                    }
                }

                if (next is null) break;

                RaiseChanged();
                await RunAsync(next).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(QueuedTransfer item)
    {
        var progress = new Progress<TransferProgress>(report =>
        {
            lock (_gate)
            {
                item.TransferredBytes = report.BytesTransferred;
                if (report.TotalBytes is { } total) item.TotalBytes = total;
            }
            RaiseChanged();
        });

        var token = item.Cancellation!.Token;
        try
        {
            if (item.Direction == TransferDirection.Upload)
                await item.Sftp.UploadAsync(item.LocalPath, item.RemotePath, progress, token).ConfigureAwait(false);
            else
                await item.Sftp.DownloadAsync(item.RemotePath, item.LocalPath, progress, token).ConfigureAwait(false);

            await FinishAsync(item, TransferItemState.Completed, error: null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(item, TransferItemState.Cancelled, error: null).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SshException or IOException or UnauthorizedAccessException)
        {
            await FinishAsync(item, TransferItemState.Failed, exception.Message).ConfigureAwait(false);
        }
    }

    private async Task FinishAsync(QueuedTransfer item, TransferItemState state, string? error)
    {
        var endedAt = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            item.State = state;
            item.Error = error;
            item.EndedAtUtc = endedAt;
            item.Cancellation = null;
            // IProgress<T>.Report marshals through the thread pool, so the final "100%"
            // report from a fast transfer can still be in flight when its task has
            // already completed. Force the byte count to match on success so a
            // completed item never shows less than 100% transferred.
            if (state == TransferItemState.Completed && item.TotalBytes is { } total)
                item.TransferredBytes = total;
        }

        await history.RecordAsync(item.ToHistoryEntry(), CancellationToken.None).ConfigureAwait(false);
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _signal.Release();
        _shutdown.Dispose();
        _signal.Dispose();
    }

    private sealed class QueuedTransfer(
        Guid id,
        ISftpSession sftp,
        Guid hostId,
        string hostLabel,
        TransferDirection direction,
        string localPath,
        string remotePath,
        long? totalBytes,
        DateTimeOffset enqueuedAtUtc)
    {
        public Guid Id { get; } = id;
        public ISftpSession Sftp { get; set; } = sftp;
        public Guid HostId { get; } = hostId;
        public string HostLabel { get; } = hostLabel;
        public TransferDirection Direction { get; } = direction;
        public string LocalPath { get; } = localPath;
        public string RemotePath { get; } = remotePath;
        public long? TotalBytes { get; set; } = totalBytes;
        public TransferItemState State { get; set; } = TransferItemState.Queued;
        public long TransferredBytes { get; set; }
        public int Attempt { get; set; } = 1;
        public string? Error { get; set; }
        public DateTimeOffset EnqueuedAtUtc { get; } = enqueuedAtUtc;
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? EndedAtUtc { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }

        public TransferQueueItem ToSnapshot() => new(
            Id, HostId, HostLabel, Direction, LocalPath, RemotePath, TotalBytes,
            State, TransferredBytes, Attempt, Error, EnqueuedAtUtc, StartedAtUtc, EndedAtUtc);

        public TransferHistoryEntry ToHistoryEntry() => new(
            Guid.NewGuid(), Id, HostId, HostLabel, Direction, LocalPath, RemotePath, TotalBytes,
            TransferredBytes, State, Error, Attempt, EnqueuedAtUtc, StartedAtUtc, EndedAtUtc ?? DateTimeOffset.UtcNow);
    }
}
