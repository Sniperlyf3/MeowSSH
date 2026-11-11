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
        Mode: PosixMode(entry.Mode),
        ModifiedAt: entry.ModifiedAt,
        // Trust the type bits over the IsDirectory flag where they disagree: a
        // symlink to a directory reports IsDirectory, and treating it as one for
        // deletion would call rmdir on the link. Mode 0 means the server sent no
        // type bits, so the flag is all there is to go on.
        IsDirectory: entry.IsDirectory && (PosixMode(entry.Mode) & 0xF000) is DirectoryType or 0);

    /// <summary>
    /// The agent's mode as the POSIX st_mode <see cref="RemoteFile"/> reads.
    /// </summary>
    /// <remarks>
    /// The Go agent sends <c>uint32(os.FileMode)</c>, whose type and special
    /// bits live above bit 16 (symlink is 1&lt;&lt;27, directory 1&lt;&lt;31), not in
    /// st_mode's 0xF000. Read as st_mode, IsSymlink was never true on a real
    /// server: the Files screen showed links as files, and Advanced SFTP's zip
    /// download followed links it exists to skip, packing the target's contents
    /// -- anything the link pointed at, inside the tree or not. A value with no
    /// bit above 0xFFFF is already st_mode (or bare permissions) and passes
    /// through unchanged.
    /// </remarks>
    internal static uint PosixMode(uint agentMode)
    {
        if (agentMode <= 0xFFFF) return agentMode;

        const uint goDir = 1u << 31, goSymlink = 1u << 27, goDevice = 1u << 26, goNamedPipe = 1u << 25,
            goSocket = 1u << 24, goSetuid = 1u << 23, goSetgid = 1u << 22, goCharDevice = 1u << 21, goSticky = 1u << 20;

        var mode = agentMode & 0x1FF;
        if ((agentMode & goSetuid) != 0) mode |= 0x800;
        if ((agentMode & goSetgid) != 0) mode |= 0x400;
        if ((agentMode & goSticky) != 0) mode |= 0x200;

        mode |= (agentMode & (goDir | goSymlink | goDevice | goNamedPipe | goSocket | goCharDevice)) switch
        {
            var t when (t & goSymlink) != 0 => 0xA000u,
            var t when (t & goDir) != 0 => 0x4000u,
            var t when (t & goSocket) != 0 => 0xC000u,
            var t when (t & goNamedPipe) != 0 => 0x1000u,
            var t when (t & goCharDevice) != 0 => 0x2000u,
            var t when (t & goDevice) != 0 => 0x6000u,
            _ => 0x8000u,
        };
        return mode;
    }

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
