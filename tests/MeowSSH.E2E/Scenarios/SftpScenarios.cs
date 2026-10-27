using System.IO.Compression;
using System.Security.Cryptography;
using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using Xunit;

namespace MeowSSH.E2E.Scenarios;

/// <summary>
/// Files: browsing, transfers, the operations behind the Files screen, the
/// transfer queue, bookmarks, recursive operations and remote search. Every
/// result is checked from the server's side through a shell, not only through
/// the SFTP session that made the change.
/// </summary>
public static class SftpScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        new("sftp.browse", BrowseHomeDirectory),
        new("sftp.upload-download.roundtrip", UploadAndDownloadAreByteExact),
        new("sftp.mkdir-rename-chmod-delete", FileOperationsReachTheServer),
        new("sftp.editor.remote-change-detected", RemoteChangeIsVisibleToTheEditorsConflictCheck),
        new("sftp.recursive.inspect-zip-delete", RecursiveInspectZipAndDelete),
        new("sftp.remote-search", RemoteSearchFindsNestedMatches),
        new("sftp.transfer-queue.complete-fail-retry", TransferQueueCompletesFailsAndRetries),
        new("sftp.bookmarks.persist", BookmarksSurviveARestart),
    ];

    /// <summary>A fresh directory on the server for one scenario, removed afterwards.</summary>
    private sealed class RemoteDir(ISshConnection ssh, string path) : IAsyncDisposable
    {
        public string Path { get; } = path;

        public static async Task<RemoteDir> CreateAsync(ISshConnection ssh, ISftpSession sftp)
        {
            var home = await sftp.ResolveAsync(".");
            var path = $"{home}/meow-e2e-{Guid.NewGuid():N}";
            await ShellAsync(ssh, $"mkdir -p '{path}'");
            return new RemoteDir(ssh, path);
        }

        public async ValueTask DisposeAsync()
        {
            try { await ssh.RunCommandAsync($"rm -rf '{Path}'"); } catch (Exception) { }
        }
    }

    private static async Task<string> ShellAsync(ISshConnection ssh, string command)
    {
        var result = await ssh.RunCommandAsync(command, TimeSpan.FromSeconds(60));
        if (!result.Succeeded)
            throw new InvalidOperationException($"'{command}' exited {result.ExitCode}: {result.StandardError}{result.StandardOutput}");
        return result.StandardOutput.Trim();
    }

    private static async Task<(E2EApp App, HostRecord Host, ISshConnection Ssh, ISftpSession Sftp)> OpenAsync(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var ssh = await app.ConnectSshAsync(host);
        return (app, host, ssh, await ssh.OpenSftpAsync());
    }

    private static async Task BrowseHomeDirectory(E2EContext c)
    {
        var (_, _, ssh, sftp) = await OpenAsync(c);
        await using var _ssh = ssh;
        await using var _sftp = sftp;

        var home = await sftp.ResolveAsync(".");
        Assert.Equal(await ShellAsync(ssh, "pwd"), home);

        await using var dir = await RemoteDir.CreateAsync(ssh, sftp);
        await ShellAsync(ssh, $"cd '{dir.Path}' && printf 12345 > file.txt && mkdir sub && ln -s file.txt link");

        var entries = (await sftp.ListAsync(dir.Path)).ToDictionary(e => e.Name);
        Assert.Equal(5, entries["file.txt"].Size);
        Assert.False(entries["file.txt"].IsDirectory);
        Assert.True(entries["sub"].IsDirectory);
        Assert.True(entries["link"].IsSymlink);
        Assert.Equal($"{dir.Path}/file.txt", entries["file.txt"].Path);
    }

    private static async Task UploadAndDownloadAreByteExact(E2EContext c)
    {
        var (app, _, ssh, sftp) = await OpenAsync(c);
        await using var _ssh = ssh;
        await using var _sftp = sftp;
        await using var dir = await RemoteDir.CreateAsync(ssh, sftp);

        // Big enough to span many SFTP packets, odd-sized so a dropped tail shows.
        var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var local = app.LocalFiles.CreateTemporaryPath("upload.bin");
        await File.WriteAllBytesAsync(local, payload);

        var uploadProgress = new LastProgress();
        await sftp.UploadAsync(local, $"{dir.Path}/upload.bin", uploadProgress);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)),
            (await ShellAsync(ssh, $"sha256sum '{dir.Path}/upload.bin'")).Split(' ')[0]);
        Assert.Equal(payload.Length, uploadProgress.Last?.BytesTransferred);

        var downloaded = app.LocalFiles.CreateTemporaryPath("download.bin");
        await sftp.DownloadAsync($"{dir.Path}/upload.bin", downloaded, new LastProgress());
        Assert.Equal(payload, await File.ReadAllBytesAsync(downloaded));
    }

    private static async Task FileOperationsReachTheServer(E2EContext c)
    {
        var (_, _, ssh, sftp) = await OpenAsync(c);
        await using var _ssh = ssh;
        await using var _sftp = sftp;
        await using var dir = await RemoteDir.CreateAsync(ssh, sftp);

        await sftp.CreateDirectoryAsync($"{dir.Path}/made");
        Assert.Equal("dir", await ShellAsync(ssh, $"test -d '{dir.Path}/made' && echo dir"));

        await ShellAsync(ssh, $"printf hi > '{dir.Path}/made/a.txt'");
        await sftp.RenameAsync($"{dir.Path}/made/a.txt", $"{dir.Path}/made/b.txt");
        Assert.Equal("hi", await ShellAsync(ssh, $"cat '{dir.Path}/made/b.txt'"));
        Assert.Equal("gone", await ShellAsync(ssh, $"test -e '{dir.Path}/made/a.txt' || echo gone"));

        await sftp.SetPermissionsAsync($"{dir.Path}/made/b.txt", Convert.ToUInt32("640", 8));
        Assert.Equal("640", await ShellAsync(ssh, $"stat -c %a '{dir.Path}/made/b.txt'"));
        Assert.Equal("rw-r-----", (await sftp.StatAsync($"{dir.Path}/made/b.txt")).PermissionString);

        await sftp.DeleteAsync(await sftp.StatAsync($"{dir.Path}/made/b.txt"));
        await sftp.DeleteAsync(await sftp.StatAsync($"{dir.Path}/made"));
        Assert.Equal("gone", await ShellAsync(ssh, $"test -e '{dir.Path}/made' || echo gone"));
    }

    private static async Task RemoteChangeIsVisibleToTheEditorsConflictCheck(E2EContext c)
    {
        // FilesPage refuses to overwrite a file whose size or mtime changed since
        // it was opened. That check is only as good as what stat reports for a
        // change made by someone else, which is what this pins down.
        var (_, _, ssh, sftp) = await OpenAsync(c);
        await using var _ssh = ssh;
        await using var _sftp = sftp;
        await using var dir = await RemoteDir.CreateAsync(ssh, sftp);
        var path = $"{dir.Path}/notes.txt";

        await ShellAsync(ssh, $"printf one > '{path}' && touch -d '2020-01-01 00:00:00' '{path}'");
        var opened = await sftp.StatAsync(path);

        // Same size, later mtime: someone saved the same length of text.
        await ShellAsync(ssh, $"printf two > '{path}'");
        var sameSize = await sftp.StatAsync(path);
        Assert.Equal(opened.Size, sameSize.Size);
        Assert.NotEqual(opened.ModifiedAt, sameSize.ModifiedAt);

        // Mtime put back, size changed: someone appended and preserved the time.
        await ShellAsync(ssh, $"printf ' more' >> '{path}' && touch -d '2020-01-01 00:00:00' '{path}'");
        var appended = await sftp.StatAsync(path);
        Assert.Equal(opened.ModifiedAt, appended.ModifiedAt);
        Assert.NotEqual(opened.Size, appended.Size);
    }

    private static async Task RecursiveInspectZipAndDelete(E2EContext c)
    {
        var (app, _, ssh, sftp) = await OpenAsync(c);
        await using var _ssh = ssh;
        await using var _sftp = sftp;
        await using var dir = await RemoteDir.CreateAsync(ssh, sftp);
        var tree = $"{dir.Path}/tree";
        await ShellAsync(ssh, $"mkdir -p '{tree}/a/b' && printf 1234 > '{tree}/top.txt' && printf 123456 > '{tree}/a/b/deep.txt' && ln -s top.txt '{tree}/link'");

        var advanced = app.Get<IAdvancedSftpService>();
        var root = await sftp.StatAsync(tree);

        var summary = await advanced.InspectDirectoryAsync(sftp, root);
        Assert.Equal(2, summary.FileCount);
        Assert.Equal(2, summary.DirectoryCount);
        Assert.Equal(1, summary.SymlinkCount);
        Assert.Equal(10, summary.TotalFileBytes);

        var zipPath = app.LocalFiles.CreateTemporaryPath("tree.zip");
        await advanced.DownloadDirectoryZipAsync(sftp, root, zipPath);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var deep = zip.Entries.Single(e => e.FullName.EndsWith("a/b/deep.txt", StringComparison.Ordinal));
            using var reader = new StreamReader(deep.Open());
            Assert.Equal("123456", await reader.ReadToEndAsync());
        }

        await advanced.DeleteDirectoryRecursiveAsync(sftp, root);
        Assert.Equal("gone", await ShellAsync(ssh, $"test -e '{tree}' || echo gone"));
        // The link was removed, not followed: its target sat beside it and is
        // gone with the tree, but nothing outside the tree may be touched.
        Assert.Equal("kept", await ShellAsync(ssh, $"test -d '{dir.Path}' && echo kept"));
    }

    private static async Task RemoteSearchFindsNestedMatches(E2EContext c)
    {
        var (app, _, ssh, sftp) = await OpenAsync(c);
        await using var _ssh = ssh;
        await using var _sftp = sftp;
        await using var dir = await RemoteDir.CreateAsync(ssh, sftp);
        await ShellAsync(ssh, $"cd '{dir.Path}' && mkdir -p x/y && touch needle.log x/y/NEEDLE-2.txt x/hay.txt");

        var results = await app.Get<ISftpRemoteSearchService>().SearchAsync(sftp, await sftp.StatAsync(dir.Path), "needle");
        var found = results.Select(r => r.RelativePath.Replace('\\', '/')).Order().ToArray();
        Assert.Equal(["needle.log", "x/y/NEEDLE-2.txt"], found);
    }

    private static async Task TransferQueueCompletesFailsAndRetries(E2EContext c)
    {
        var (app, host, ssh, sftp) = await OpenAsync(c);
        await using var _ssh = ssh;
        await using var _sftp = sftp;
        await using var dir = await RemoteDir.CreateAsync(ssh, sftp);
        var queue = app.Get<ITransferQueueService>();

        var payload = RandomNumberGenerator.GetBytes(256 * 1024);
        var local = app.LocalFiles.CreateTemporaryPath("queued.bin");
        await File.WriteAllBytesAsync(local, payload);
        var upload = await queue.EnqueueAsync(sftp, host.Id, host.Label, TransferDirection.Upload, local, $"{dir.Path}/queued.bin", payload.Length);
        Assert.Equal(TransferItemState.Completed, (await WaitFinishedAsync(queue, upload.Id)).State);
        Assert.Equal(payload.Length.ToString(), await ShellAsync(ssh, $"stat -c %s '{dir.Path}/queued.bin'"));

        // A download of something not there yet fails with a reason, and Retry
        // succeeds once it exists: the queue's whole reason to exist.
        var target = app.LocalFiles.CreateTemporaryPath("late.txt");
        var download = await queue.EnqueueAsync(sftp, host.Id, host.Label, TransferDirection.Download, target, $"{dir.Path}/late.txt", null);
        var failed = await WaitFinishedAsync(queue, download.Id);
        Assert.Equal(TransferItemState.Failed, failed.State);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));

        await ShellAsync(ssh, $"printf arrived > '{dir.Path}/late.txt'");
        var retried = await queue.RetryAsync(download.Id, sftp);
        var done = await WaitFinishedAsync(queue, retried.Id);
        Assert.Equal(TransferItemState.Completed, done.State);
        Assert.True(done.Attempt >= 2, $"retry did not count as a new attempt ({done.Attempt})");
        Assert.Equal("arrived", await File.ReadAllTextAsync(target));

        // History is what survives the session: the finished uploads and the failure.
        var history = await app.Get<ITransferHistoryService>().GetAllAsync();
        Assert.Contains(history, h => h.TransferId == upload.Id && h.State == TransferItemState.Completed);
        Assert.Contains(history, h => h.TransferId == download.Id && h.State == TransferItemState.Failed);
    }

    private static async Task BookmarksSurviveARestart(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await app.Get<ISftpBookmarkService>().SaveAsync(host.Id, "logs", "/var/log");

        app = await c.RestartAsync(app);
        var bookmark = Assert.Single(await app.Get<ISftpBookmarkService>().GetForHostAsync(host.Id));
        Assert.Equal("/var/log", bookmark.Path);

        // And it still opens: the host and its key came back out of the vault too.
        await using var ssh = await app.ConnectSshAsync(host);
        await using var sftp = await ssh.OpenSftpAsync();
        Assert.NotEmpty(await sftp.ListAsync(bookmark.Path));
    }

    private static async Task<TransferQueueItem> WaitFinishedAsync(ITransferQueueService queue, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var item = queue.Items.SingleOrDefault(i => i.Id == id);
            if (item is { IsFinished: true }) return item;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Transfer {id} did not finish: {queue.Items.SingleOrDefault(i => i.Id == id)}");
    }

    private sealed class LastProgress : IProgress<TransferProgress>
    {
        public TransferProgress? Last { get; private set; }
        public void Report(TransferProgress value) => Last = value;
    }
}
