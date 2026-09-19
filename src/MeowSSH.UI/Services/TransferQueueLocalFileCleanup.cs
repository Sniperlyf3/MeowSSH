using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.UI.Services;

/// <summary>
/// Finishes what a queued transfer cannot finish for itself: publishing a completed download to
/// where the user can actually find it, and deleting the app-owned temporary file either way
/// (the picked local copy for an upload, or the download target once it has been published or
/// abandoned).
/// </summary>
/// <remarks>
/// Deliberately not part of <c>FilesPage</c>. The whole point of the transfer queue is that a
/// transfer keeps running after the page that queued it has been navigated away from and
/// disposed, so this has to live at least as long as the queue does, not as long as any one page
/// does -- a page-scoped handler would silently orphan a downloaded temp file (never published,
/// never deleted) for anything that finished after the user left. <c>FilesPage</c> only needs to
/// inject this to keep it alive from the moment a queue item can first be created; it never calls
/// into it directly.
/// </remarks>
public sealed class TransferQueueLocalFileCleanup : IDisposable
{
    private readonly ITransferQueueService _queue;
    private readonly ILocalFileTransferService _localFiles;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _handled = [];
    private bool _disposed;

    public TransferQueueLocalFileCleanup(ITransferQueueService queue, ILocalFileTransferService localFiles)
    {
        _queue = queue;
        _localFiles = localFiles;
        _queue.Changed += OnChanged;
        // Something may already have finished between the queue's own construction and this
        // subscription (e.g. a fast transfer enqueued before anything observed it).
        OnChanged(this, EventArgs.Empty);
    }

    private void OnChanged(object? sender, EventArgs e) => _ = HandleFinishedAsync();

    private async Task HandleFinishedAsync()
    {
        List<TransferQueueItem> newlyFinished;
        lock (_gate)
        {
            if (_disposed) return;
            newlyFinished = [.. _queue.Items.Where(item => item.IsFinished && _handled.Add(item.Id))];
        }

        foreach (var item in newlyFinished)
        {
            if (item.Direction == TransferDirection.Download && item.State == TransferItemState.Completed)
            {
                try
                {
                    await _localFiles.PublishDownloadAsync(item.LocalPath, RemotePath.NameOf(item.RemotePath)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Nothing left to notify -- the page that queued this may be long gone.
                    // The temp file below is still cleaned up either way.
                }
            }

            _localFiles.DeleteTemporaryFile(item.LocalPath);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _queue.Changed -= OnChanged;
    }
}
