using MeowSSH.Core.Ssh;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// An in-memory filesystem with no server behind it, so the file browser can be
/// driven in a browser on CI.
/// </summary>
/// <remarks>
/// It enforces the rules the UI has to cope with rather than answering yes to
/// everything: a missing path is <see cref="SshFailure.NotFound"/>, an
/// unreadable one is <see cref="SshFailure.PermissionDenied"/>, and a directory
/// with children refuses to be deleted. A fake that always succeeded would leave
/// every error path in the UI untested.
/// </remarks>
public sealed class FakeSftpSession : ISftpSession
{
    private readonly Dictionary<string, List<RemoteFile>> _tree = [];

    public const string Home = "/home/deploy";

    /// <summary>Paths this user is not allowed to read.</summary>
    private static readonly string[] Forbidden = ["/root"];

    public FakeSftpSession()
    {
        var now = DateTimeOffset.UtcNow;

        RemoteFile Dir(string parent, string name, TimeSpan age) =>
            new(name, RemotePath.Join(parent, name), 0, 0x41ED, now - age, IsDirectory: true);
        RemoteFile File(string parent, string name, long size, uint mode, TimeSpan age) =>
            new(name, RemotePath.Join(parent, name), size, mode, now - age, IsDirectory: false);

        _tree["/"] = [Dir("/", "home", TimeSpan.FromDays(400)), Dir("/", "etc", TimeSpan.FromDays(400)), Dir("/", "var", TimeSpan.FromDays(400))];
        _tree["/home"] = [Dir("/home", "deploy", TimeSpan.FromDays(90))];
        _tree[Home] =
        [
            Dir(Home, "releases", TimeSpan.FromHours(3)),
            Dir(Home, ".config", TimeSpan.FromDays(45)),
            File(Home, "start.sh", 402, 0x81ED, TimeSpan.FromHours(2)),          // 0755
            File(Home, "app.log", 18_442_240, 0x8180, TimeSpan.FromMinutes(4)),  // 0600
            File(Home, "backup-2026-09.tar.gz", 1_205_000_000, 0x81A4, TimeSpan.FromDays(2)),
            File(Home, ".bashrc", 3_771, 0x81A4, TimeSpan.FromDays(120)),
            new("current", RemotePath.Join(Home, "current"), 0, 0xA1FF, now - TimeSpan.FromHours(3), IsDirectory: false),
        ];
        _tree[RemotePath.Join(Home, "releases")] =
        [
            Dir(RemotePath.Join(Home, "releases"), "2026-09-11", TimeSpan.FromHours(3)),
            Dir(RemotePath.Join(Home, "releases"), "2026-09-04", TimeSpan.FromDays(8)),
        ];
        _tree[RemotePath.Join(Home, "releases/2026-09-11")] = [];
        _tree[RemotePath.Join(Home, "releases/2026-09-04")] = [];
        _tree[RemotePath.Join(Home, ".config")] = [File(RemotePath.Join(Home, ".config"), "settings.toml", 214, 0x81A4, TimeSpan.FromDays(45))];
        _tree["/etc"] = [];
        _tree["/var"] = [];
    }

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Forbidden.Contains(path))
            throw new SshException(SshFailure.PermissionDenied, $"permission denied: {path}");
        if (!_tree.TryGetValue(path, out var entries))
            throw new SshException(SshFailure.NotFound, $"no such file or directory: {path}");
        return Task.FromResult<IReadOnlyList<RemoteFile>>([.. entries]);
    }

    public Task<RemoteFile> StatAsync(string path, CancellationToken cancellationToken = default)
    {
        var parent = RemotePath.ParentOf(path);
        if (parent is not null && _tree.TryGetValue(parent, out var siblings))
        {
            var match = siblings.FirstOrDefault(e => e.Path == path);
            if (match is not null) return Task.FromResult(match);
        }
        throw new SshException(SshFailure.NotFound, $"no such file or directory: {path}");
    }

    public Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(path == "." ? Home : path);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var parent = RemotePath.ParentOf(path) ?? "/";
        if (!_tree.TryGetValue(parent, out var siblings))
            throw new SshException(SshFailure.NotFound, $"no such file or directory: {parent}");
        if (siblings.Any(e => e.Path == path))
            throw new SshException(SshFailure.Unknown, $"already exists: {path}");

        siblings.Add(new RemoteFile(RemotePath.NameOf(path), path, 0, 0x41ED, DateTimeOffset.UtcNow, IsDirectory: true));
        _tree[path] = [];
        return Task.CompletedTask;
    }

    public Task DeleteAsync(RemoteFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.IsDirectory && _tree.TryGetValue(file.Path, out var children) && children.Count > 0)
            throw new SshException(SshFailure.Unknown, $"directory not empty: {file.Path}");

        var parent = RemotePath.ParentOf(file.Path) ?? "/";
        if (_tree.TryGetValue(parent, out var siblings)) siblings.RemoveAll(e => e.Path == file.Path);
        _tree.Remove(file.Path);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        var parent = RemotePath.ParentOf(path) ?? "/";
        if (!_tree.TryGetValue(parent, out var siblings)) throw new SshException(SshFailure.NotFound, path);
        var index = siblings.FindIndex(e => e.Path == path);
        if (index < 0) throw new SshException(SshFailure.NotFound, path);

        siblings[index] = siblings[index] with { Name = RemotePath.NameOf(newPath), Path = newPath };
        return Task.CompletedTask;
    }

    public Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken = default)
    {
        var parent = RemotePath.ParentOf(path) ?? "/";
        if (!_tree.TryGetValue(parent, out var siblings)) throw new SshException(SshFailure.NotFound, path);
        var index = siblings.FindIndex(e => e.Path == path);
        if (index < 0) throw new SshException(SshFailure.NotFound, path);

        // Keep the type bits; chmod only sets the low twelve.
        siblings[index] = siblings[index] with { Mode = (siblings[index].Mode & 0xF000) | (mode & 0xFFF) };
        return Task.CompletedTask;
    }

    public Task UploadAsync(string localPath, string remotePath,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new TransferProgress(1024, 1024));
        return Task.CompletedTask;
    }

    public Task DownloadAsync(string remotePath, string localPath,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new TransferProgress(1024, 1024));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
