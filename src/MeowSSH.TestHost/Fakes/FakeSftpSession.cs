using MeowSSH.Core.Ssh;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// An in-memory filesystem with no server behind it, so the file browser can be
/// driven in a browser on CI.
/// </summary>
public sealed class FakeSftpSession : ISftpSession
{
    private readonly Dictionary<string, List<RemoteFile>> _tree = [];
    private readonly Dictionary<string, byte[]> _contents = [];

    public const string Home = "/home/deploy";

    private static readonly string[] Forbidden = ["/root"];

    public FakeSftpSession()
    {
        var now = DateTimeOffset.UtcNow;

        RemoteFile Dir(string parent, string name, TimeSpan age) =>
            new(name, RemotePath.Join(parent, name), 0, 0x41ED, now - age, IsDirectory: true);
        RemoteFile FileEntry(string parent, string name, long size, uint mode, TimeSpan age) =>
            new(name, RemotePath.Join(parent, name), size, mode, now - age, IsDirectory: false);

        _tree["/"] = [Dir("/", "home", TimeSpan.FromDays(400)), Dir("/", "etc", TimeSpan.FromDays(400)), Dir("/", "var", TimeSpan.FromDays(400))];
        _tree["/home"] = [Dir("/home", "deploy", TimeSpan.FromDays(90))];
        _tree[Home] =
        [
            Dir(Home, "releases", TimeSpan.FromHours(3)),
            Dir(Home, ".config", TimeSpan.FromDays(45)),
            FileEntry(Home, "start.sh", 402, 0x81ED, TimeSpan.FromHours(2)),
            FileEntry(Home, "app.log", 18_442_240, 0x8180, TimeSpan.FromMinutes(4)),
            FileEntry(Home, "backup-2026-09.tar.gz", 1_205_000_000, 0x81A4, TimeSpan.FromDays(2)),
            FileEntry(Home, ".bashrc", 3_771, 0x81A4, TimeSpan.FromDays(120)),
            new("current", RemotePath.Join(Home, "current"), 0, 0xA1FF, now - TimeSpan.FromHours(3), IsDirectory: false),
        ];
        _tree[RemotePath.Join(Home, "releases")] =
        [
            Dir(RemotePath.Join(Home, "releases"), "2026-09-11", TimeSpan.FromHours(3)),
            Dir(RemotePath.Join(Home, "releases"), "2026-09-04", TimeSpan.FromDays(8)),
        ];
        _tree[RemotePath.Join(Home, "releases/2026-09-11")] = [];
        _tree[RemotePath.Join(Home, "releases/2026-09-04")] = [];
        _tree[RemotePath.Join(Home, ".config")] = [FileEntry(RemotePath.Join(Home, ".config"), "settings.toml", 214, 0x81A4, TimeSpan.FromDays(45))];
        _tree["/etc"] = [];
        _tree["/var"] = [];

        _contents[RemotePath.Join(Home, "start.sh")] = System.Text.Encoding.UTF8.GetBytes("#!/bin/sh\necho hello from MeowSSH\n");
        _contents[RemotePath.Join(Home, ".bashrc")] = System.Text.Encoding.UTF8.GetBytes("export EDITOR=vi\n");
        _contents[RemotePath.Join(Home, ".config/settings.toml")] = System.Text.Encoding.UTF8.GetBytes("theme = \"dark\"\n");
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
        _contents.Remove(file.Path);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        var sourceParent = RemotePath.ParentOf(path) ?? "/";
        var destinationParent = RemotePath.ParentOf(newPath) ?? "/";
        if (!_tree.TryGetValue(sourceParent, out var sourceSiblings))
            throw new SshException(SshFailure.NotFound, path);
        if (!_tree.TryGetValue(destinationParent, out var destinationSiblings))
            throw new SshException(SshFailure.NotFound, destinationParent);

        var index = sourceSiblings.FindIndex(e => e.Path == path);
        if (index < 0) throw new SshException(SshFailure.NotFound, path);
        if (destinationSiblings.Any(e => e.Path == newPath))
            throw new SshException(SshFailure.Unknown, $"already exists: {newPath}");

        var source = sourceSiblings[index];
        sourceSiblings.RemoveAt(index);
        destinationSiblings.Add(source with { Name = RemotePath.NameOf(newPath), Path = newPath });

        if (source.IsDirectory && _tree.ContainsKey(path))
        {
            var treeMoves = _tree
                .Where(pair => pair.Key == path || pair.Key.StartsWith(path + "/", StringComparison.Ordinal))
                .OrderBy(pair => pair.Key.Length)
                .ToArray();

            foreach (var pair in treeMoves)
                _tree.Remove(pair.Key);

            foreach (var pair in treeMoves)
            {
                var movedDirectoryPath = newPath + pair.Key[path.Length..];
                _tree[movedDirectoryPath] =
                [
                    .. pair.Value.Select(entry =>
                    {
                        var movedEntryPath = newPath + entry.Path[path.Length..];
                        return entry with { Path = movedEntryPath };
                    })
                ];
            }
        }

        var contentMoves = _contents
            .Where(pair => pair.Key == path || pair.Key.StartsWith(path + "/", StringComparison.Ordinal))
            .ToArray();
        foreach (var pair in contentMoves)
            _contents.Remove(pair.Key);
        foreach (var pair in contentMoves)
            _contents[newPath + pair.Key[path.Length..]] = pair.Value;

        return Task.CompletedTask;
    }

    public Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken = default)
    {
        var parent = RemotePath.ParentOf(path) ?? "/";
        if (!_tree.TryGetValue(parent, out var siblings)) throw new SshException(SshFailure.NotFound, path);
        var index = siblings.FindIndex(e => e.Path == path);
        if (index < 0) throw new SshException(SshFailure.NotFound, path);

        siblings[index] = siblings[index] with { Mode = (siblings[index].Mode & 0xF000) | (mode & 0xFFF) };
        return Task.CompletedTask;
    }

    public async Task UploadAsync(string localPath, string remotePath,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
        _contents[remotePath] = bytes;

        var parent = RemotePath.ParentOf(remotePath) ?? "/";
        if (!_tree.TryGetValue(parent, out var siblings))
            throw new SshException(SshFailure.NotFound, parent);
        var index = siblings.FindIndex(e => e.Path == remotePath);
        var entry = new RemoteFile(RemotePath.NameOf(remotePath), remotePath, bytes.LongLength, 0x81A4, DateTimeOffset.UtcNow, IsDirectory: false);
        if (index >= 0) siblings[index] = entry with { Mode = siblings[index].Mode };
        else siblings.Add(entry);

        progress?.Report(new TransferProgress(bytes.LongLength, bytes.LongLength));
    }

    public async Task DownloadAsync(string remotePath, string localPath,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!_contents.TryGetValue(remotePath, out var bytes))
            bytes = System.Text.Encoding.UTF8.GetBytes($"fake contents of {remotePath}\n");
        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
        progress?.Report(new TransferProgress(bytes.LongLength, bytes.LongLength));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
