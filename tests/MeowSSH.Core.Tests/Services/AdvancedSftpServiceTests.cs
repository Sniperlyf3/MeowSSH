using System.IO.Compression;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Services;

public sealed class AdvancedSftpServiceTests
{
    [Fact]
    public async Task FreeTierIsDeniedBeforeRemoteTraversal()
    {
        var sftp = new RecordingSftp();
        var service = new AdvancedSftpService(new FakeEntitlements(pro: false));
        var root = sftp.Root;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InspectDirectoryAsync(sftp, root));

        Assert.Contains("Pro", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, sftp.ListCalls);
    }

    [Fact]
    public async Task RecursiveDeleteDeletesChildrenBeforeParentsAndDoesNotTraverseSymlink()
    {
        var sftp = new RecordingSftp();
        sftp.AddDirectory("/data/child");
        sftp.AddFile("/data/child/a.txt", "alpha");
        sftp.AddFile("/data/top.txt", "top");
        sftp.AddSymlink("/data/outside-link");
        var service = new AdvancedSftpService(new FakeEntitlements(pro: true));

        await service.DeleteDirectoryRecursiveAsync(sftp, sftp.Root);

        Assert.Equal(
        [
            "/data/child/a.txt",
            "/data/child",
            "/data/top.txt",
            "/data/outside-link",
            "/data",
        ], sftp.DeletedPaths);
        Assert.DoesNotContain("/data/outside-link", sftp.ListedPaths);
    }

    [Fact]
    public async Task FolderZipPreservesRelativePathsAndSkipsSymlinks()
    {
        var sftp = new RecordingSftp();
        sftp.AddDirectory("/data/config");
        sftp.AddFile("/data/config/app.conf", "hello");
        sftp.AddFile("/data/readme.txt", "read me");
        sftp.AddSymlink("/data/current");
        var service = new AdvancedSftpService(new FakeEntitlements(pro: true));
        var zip = Path.Combine(Path.GetTempPath(), $"meowssh-sftp-{Guid.NewGuid():N}.zip");

        try
        {
            await service.DownloadDirectoryZipAsync(sftp, sftp.Root, zip);

            using var archive = ZipFile.OpenRead(zip);
            Assert.NotNull(archive.GetEntry("config/"));
            Assert.NotNull(archive.GetEntry("config/app.conf"));
            Assert.NotNull(archive.GetEntry("readme.txt"));
            Assert.Null(archive.GetEntry("current"));
            Assert.Equal(["/data/config/app.conf", "/data/readme.txt"], sftp.DownloadedPaths.Order().ToArray());
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task InspectionReportsFilesDirectoriesSymlinksAndBytes()
    {
        var sftp = new RecordingSftp();
        sftp.AddDirectory("/data/one");
        sftp.AddFile("/data/one/a", "1234");
        sftp.AddFile("/data/b", "123456");
        sftp.AddSymlink("/data/link");
        var service = new AdvancedSftpService(new FakeEntitlements(pro: true));

        var summary = await service.InspectDirectoryAsync(sftp, sftp.Root);

        Assert.Equal(2, summary.FileCount);
        Assert.Equal(1, summary.DirectoryCount);
        Assert.Equal(1, summary.SymlinkCount);
        Assert.Equal(10, summary.TotalFileBytes);
    }

    [Fact]
    public async Task TraversalStopsAtEntrySafetyLimit()
    {
        var sftp = new RecordingSftp();
        for (var index = 0; index <= AdvancedSftpService.MaxEntries; index++)
            sftp.AddFile($"/data/file-{index}", "x");
        var service = new AdvancedSftpService(new FakeEntitlements(pro: true));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InspectDirectoryAsync(sftp, sftp.Root));

        Assert.Contains("safety limit", error.Message, StringComparison.OrdinalIgnoreCase);
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

    private sealed class RecordingSftp : ISftpSession
    {
        private readonly Dictionary<string, List<RemoteFile>> _tree = [];
        private readonly Dictionary<string, byte[]> _contents = [];
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public RecordingSftp()
        {
            _tree["/data"] = [];
            Root = DirectoryEntry("/data");
        }

        public RemoteFile Root { get; }
        public int ListCalls { get; private set; }
        public List<string> ListedPaths { get; } = [];
        public List<string> DeletedPaths { get; } = [];
        public List<string> DownloadedPaths { get; } = [];

        public void AddDirectory(string path)
        {
            AddEntry(DirectoryEntry(path));
            _tree[path] = [];
        }

        public void AddFile(string path, string content)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            AddEntry(new RemoteFile(RemotePath.NameOf(path), path, bytes.LongLength, 0x81A4, _now, false));
            _contents[path] = bytes;
        }

        public void AddSymlink(string path) =>
            AddEntry(new RemoteFile(RemotePath.NameOf(path), path, 12, 0xA1FF, _now, false));

        private RemoteFile DirectoryEntry(string path) =>
            new(RemotePath.NameOf(path), path, 0, 0x41ED, _now, true);

        private void AddEntry(RemoteFile entry)
        {
            var parent = RemotePath.ParentOf(entry.Path) ?? "/";
            if (!_tree.TryGetValue(parent, out var siblings))
                throw new InvalidOperationException($"Missing parent {parent} in test tree.");
            siblings.Add(entry);
        }

        public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            ListedPaths.Add(path);
            if (!_tree.TryGetValue(path, out var entries)) throw new SshException(SshFailure.NotFound, path);
            return Task.FromResult<IReadOnlyList<RemoteFile>>([.. entries]);
        }

        public Task<RemoteFile> StatAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path == Root.Path) return Task.FromResult(Root);
            var parent = RemotePath.ParentOf(path) ?? "/";
            var item = _tree.GetValueOrDefault(parent)?.FirstOrDefault(entry => entry.Path == path)
                ?? throw new SshException(SshFailure.NotFound, path);
            return Task.FromResult(item);
        }

        public Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(path);
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(RemoteFile file, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.IsDirectory && _tree.TryGetValue(file.Path, out var children) && children.Count > 0)
                throw new SshException(SshFailure.Unknown, "directory not empty");
            DeletedPaths.Add(file.Path);
            var parent = RemotePath.ParentOf(file.Path) ?? "/";
            if (_tree.TryGetValue(parent, out var siblings)) siblings.RemoveAll(item => item.Path == file.Path);
            _tree.Remove(file.Path);
            _contents.Remove(file.Path);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task DownloadAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadedPaths.Add(remotePath);
            var bytes = _contents[remotePath];
            await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
            progress?.Report(new TransferProgress(bytes.Length, bytes.Length));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
