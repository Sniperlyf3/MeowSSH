namespace MeowSSH.UI.Services;

/// <summary>A local file selected for upload, copied into app-owned temporary storage.</summary>
public sealed record LocalFileSelection(string TemporaryPath, string Name);

/// <summary>
/// Bridges the shared SFTP UI to platform file picking and public downloads.
/// Remote transfer remains in <c>ISftpSession</c>; this service only handles the
/// local side of the operation.
/// </summary>
public interface ILocalFileTransferService
{
    Task<LocalFileSelection?> PickFileAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns an app-owned temporary path suitable as an SFTP download target.</summary>
    string CreateTemporaryPath(string suggestedName);

    /// <summary>
    /// Publishes a completed temporary download somewhere the user can reach and
    /// returns a human-readable destination description.
    /// </summary>
    Task<string> PublishDownloadAsync(
        string temporaryPath,
        string suggestedName,
        CancellationToken cancellationToken = default);

    void DeleteTemporaryFile(string path);
}

/// <summary>Browser/test-host implementation. Picking is deliberately unavailable.</summary>
public sealed class LocalFileTransferService : ILocalFileTransferService
{
    public Task<LocalFileSelection?> PickFileAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<LocalFileSelection?>(null);

    public string CreateTemporaryPath(string suggestedName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "meowssh");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}-{SafeName(suggestedName)}");
    }

    public Task<string> PublishDownloadAsync(
        string temporaryPath,
        string suggestedName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(temporaryPath);

    public void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private static string SafeName(string name) =>
        string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
