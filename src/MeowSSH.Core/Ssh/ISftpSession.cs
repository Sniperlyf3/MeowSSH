namespace MeowSSH.Core.Ssh;

/// <summary>
/// File operations on a host, over the same connection its shell uses.
/// </summary>
/// <remarks>
/// Opened from an <see cref="ISshConnection"/> rather than connected separately,
/// so browsing files costs no second authentication.
/// </remarks>
public interface ISftpSession : IAsyncDisposable
{
    /// <summary>Lists a directory.</summary>
    /// <exception cref="SshException">
    /// The path does not exist (<see cref="SshFailure.NotFound"/>) or is not
    /// readable by this user (<see cref="SshFailure.PermissionDenied"/>).
    /// </exception>
    Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken cancellationToken = default);

    Task<RemoteFile> StatAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a path to its absolute, symlink-free form.
    /// </summary>
    /// <remarks>
    /// Also the way to find where to start: SFTP has no "home directory" request,
    /// but resolving <c>.</c> returns the directory the session opened in.
    /// </remarks>
    Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file, or an empty directory.</summary>
    /// <remarks>
    /// Files and directories are separate SFTP operations, so this checks which
    /// it is rather than making the caller know. A non-empty directory fails —
    /// recursive deletion is the caller's decision to make explicitly, not a
    /// surprise hidden behind a delete button.
    /// </remarks>
    Task DeleteAsync(RemoteFile file, CancellationToken cancellationToken = default);

    Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default);

    Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken = default);

    /// <summary>Uploads a local file, reporting progress as it goes.</summary>
    Task UploadAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads a remote file, reporting progress as it goes.</summary>
    Task DownloadAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
