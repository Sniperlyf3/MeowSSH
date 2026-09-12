using Meowshell;

namespace MeowSSH.Core.Ssh;

internal sealed class MeowshellSftpSession(MeowshellAgentConnection agent) : ISftpSession
{
    private const uint DirectoryType = 0x4000;

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        var entries = await Guard(() => agent.ListFilesAsync(path, cancellationToken)).ConfigureAwait(false);
        return [.. entries.Select(e => ToRemoteFile(e, RemotePath.Join(path, e.Name)))];
    }

    public async Task<RemoteFile> StatAsync(string path, CancellationToken cancellationToken = default)
    {
        var entry = await Guard(() => agent.StatAsync(path, followSymlink: true, cancellationToken)).ConfigureAwait(false);
        return ToRemoteFile(entry, path);
    }

    public Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) =>
        Guard(() => agent.RealPathAsync(path, cancellationToken));

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        Guard(() => agent.MkdirAsync(path, recursive: false, cancellationToken));

    public Task DeleteAsync(RemoteFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        // Two different SFTP operations. A symlink to a directory is removed as a
        // file: deleting what it points at instead would be a surprise, and
        // rmdir on the link fails anyway.
        return file is { IsDirectory: true, IsSymlink: false }
            ? Guard(() => agent.RemoveDirectoryAsync(file.Path, cancellationToken))
            : Guard(() => agent.RemoveAsync(file.Path, cancellationToken));
    }

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default) =>
        Guard(() => agent.RenameAsync(path, newPath, cancellationToken));

    public Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken = default) =>
        Guard(() => agent.ChmodAsync(path, mode, cancellationToken));

    public Task UploadAsync(
        string localPath, string remotePath,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // The engine reports only bytes sent for an upload, with no total, so the
        // total comes from the local file the caller already has on disk. Without
        // it the UI could show a rate but never a percentage.
        var total = new FileInfo(localPath).Length;
        var relay = progress is null
            ? null
            : new Relay<long>(done => progress.Report(new TransferProgress(done, total)));

        return Guard(() => agent.UploadAsync(localPath, remotePath, preserve: true, relay, cancellationToken));
    }

    public Task DownloadAsync(
        string remotePath, string localPath,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var relay = progress is null
            ? null
            : new Relay<(long Done, long Total)>(p => progress.Report(new TransferProgress(p.Done, p.Total)));

        return Guard(() => agent.DownloadAsync(remotePath, localPath, preserve: true, relay, cancellationToken));
    }

    private static RemoteFile ToRemoteFile(MeowshellSftpEntry entry, string path) => new(
        Name: entry.Name,
        Path: path,
        Size: entry.Size,
        Mode: entry.Mode,
        ModifiedAt: entry.ModifiedAt,
        // Trust the type bits over the IsDirectory flag where they disagree: a
        // symlink to a directory reports IsDirectory, and treating it as one for
        // deletion would call rmdir on the link. Mode 0 means the server sent no
        // type bits, so the flag is all there is to go on.
        IsDirectory: entry.IsDirectory && (entry.Mode & 0xF000) is DirectoryType or 0);

    private static async Task<T> Guard<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (TailcatException ex)
        {
            throw MeowshellSshEngine.Translate(ex);
        }
    }

    private static async Task Guard(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (TailcatException ex)
        {
            throw MeowshellSshEngine.Translate(ex);
        }
    }

    /// <summary>Adapts a callback to <see cref="IProgress{T}"/>.</summary>
    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    // The SFTP subsystem belongs to the connection, which owns its own teardown;
    // closing it here would take the shell down with it.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
