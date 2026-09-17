using MeowSSH.UI.Services;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Browser-test file picker. Each pick produces an app-owned temporary file
/// named start.sh so the SFTP UI can exercise the existing-file conflict path
/// without a platform file-picker dialog.
/// </summary>
public sealed class FakeLocalFileTransferService : ILocalFileTransferService
{
    public async Task<LocalFileSelection?> PickFileAsync(CancellationToken cancellationToken = default)
    {
        var path = CreateTemporaryPath("start.sh");
        await File.WriteAllTextAsync(path, "#!/bin/sh\necho uploaded replacement\n", cancellationToken);
        return new LocalFileSelection(path, "start.sh");
    }

    public string CreateTemporaryPath(string suggestedName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "meowssh-testhost-files");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}-{Path.GetFileName(suggestedName)}");
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
}
