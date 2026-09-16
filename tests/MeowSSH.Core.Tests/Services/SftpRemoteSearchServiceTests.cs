using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Services;

public sealed class SftpRemoteSearchServiceTests
{
    [Fact]
    public async Task FreeTierFailsBeforeRemoteListing()
    {
        var sftp = new FakeSftp();
        var service = new SftpRemoteSearchService(new FakeEntitlements(false));
        var root = Dir("/home", "deploy");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(sftp, root, "config"));

        Assert.Contains("Pro", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, sftp.ListCalls);
    }

    [Fact]
    public async Task SearchMatchesNestedNamesAndDoesNotFollowSymlinks()
    {
        var sftp = new FakeSftp();
        var service = new SftpRemoteSearchService(new FakeEntitlements(true));
        var root = Dir("/home", "deploy");

        var results = await service.SearchAsync(sftp, root, "settings");

        var result = Assert.Single(results);
        Assert.Equal(".config/settings.toml", result.RelativePath);
        Assert.Equal("settings.toml", result.File.Name);
        Assert.DoesNotContain("/home/deploy/current", sftp.ListedPaths);
    }

    [Fact]
    public async Task SearchRequiresAtLeastTwoCharacters()
    {
        var service = new SftpRemoteSearchService(new FakeEntitlements(true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new FakeSftp(), Dir("/home", "deploy"), "x"));
    }

    private static RemoteFile Dir(string parent, string name) =>
        new(name, parent == "/" ? $"/{name}" : $"{parent}/{name}", 0, 0x41ED, DateTimeOffset.UtcNow, true);

    private sealed class FakeSftp : ISftpSession
    {
        public int ListCalls { get; private set; }
        public List<string> ListedPaths { get; } = [];

        public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            ListedPaths.Add(path);
            IReadOnlyList<RemoteFile> result = path switch
            {
                "/home/deploy" =>
                [
                    Dir("/home/deploy", ".config"),
                    new RemoteFile("current", "/home/deploy/current", 0, 0xA1FF, DateTimeOffset.UtcNow, false),
                ],
                "/home/deploy/.config" =>
                [
                    new RemoteFile("settings.toml", "/home/deploy/.config/settings.toml", 12, 0x81A4, DateTimeOffset.UtcNow, false),
                ],
                _ => [],
            };
            return Task.FromResult(result);
        }

        public Task<RemoteFile> StatAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(path);
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(RemoteFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEntitlements(bool pro) : IEntitlementService
    {
        public EntitlementSnapshot Current { get; } = pro
            ? new(EntitlementTier.Pro, EntitlementSource.Promotional, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1))
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);
        public event EventHandler? Changed { add { } remove { } }
        public bool Has(PremiumFeature feature) => pro && feature == PremiumFeature.AdvancedSftp;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }
}
