using System.IO.Compression;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

public sealed record AdvancedSftpSummary(
    int FileCount,
    int DirectoryCount,
    int SymlinkCount,
    long TotalFileBytes)
{
    public int TotalEntries => FileCount + DirectoryCount + SymlinkCount;
}

public sealed record AdvancedSftpProgress(
    int CompletedItems,
    int TotalItems,
    long CompletedBytes,
    long TotalBytes,
    string CurrentPath)
{
    public double? Fraction => TotalBytes > 0
        ? Math.Clamp((double)CompletedBytes / TotalBytes, 0d, 1d)
        : TotalItems > 0 ? Math.Clamp((double)CompletedItems / TotalItems, 0d, 1d) : null;
}

public interface IAdvancedSftpService
{
    Task<AdvancedSftpSummary> InspectDirectoryAsync(
        ISftpSession sftp,
        RemoteFile directory,
        CancellationToken cancellationToken = default);

    Task DeleteDirectoryRecursiveAsync(
        ISftpSession sftp,
        RemoteFile directory,
        IProgress<AdvancedSftpProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DownloadDirectoryZipAsync(
        ISftpSession sftp,
        RemoteFile directory,
        string zipPath,
        IProgress<AdvancedSftpProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Premium recursive SFTP workflows built only from the ordinary SFTP primitive operations.
/// Traversal is deliberately bounded and never follows symbolic links as directories.
/// </summary>
public sealed class AdvancedSftpService(IEntitlementService entitlements) : IAdvancedSftpService
{
    internal const int MaxDepth = 32;
    internal const int MaxEntries = 5_000;
    internal const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    public async Task<AdvancedSftpSummary> InspectDirectoryAsync(
        ISftpSession sftp,
        RemoteFile directory,
        CancellationToken cancellationToken = default)
    {
        RequirePro();
        var tree = await BuildTreeAsync(sftp, directory, cancellationToken).ConfigureAwait(false);
        return Summary(tree);
    }

    public async Task DeleteDirectoryRecursiveAsync(
        ISftpSession sftp,
        RemoteFile directory,
        IProgress<AdvancedSftpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequirePro();
        var tree = await BuildTreeAsync(sftp, directory, cancellationToken).ConfigureAwait(false);
        var summary = Summary(tree);
        var totalItems = summary.TotalEntries + 1; // include the root directory
        var completed = 0;
        long completedBytes = 0;

        foreach (var node in tree
                     .OrderByDescending(static node => node.Depth)
                     .ThenByDescending(static node => node.File.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sftp.DeleteAsync(node.File, cancellationToken).ConfigureAwait(false);
            completed++;
            if (!node.File.IsDirectory && !node.File.IsSymlink)
                completedBytes += Math.Max(0, node.File.Size);
            progress?.Report(new AdvancedSftpProgress(
                completed,
                totalItems,
                completedBytes,
                summary.TotalFileBytes,
                node.File.Path));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await sftp.DeleteAsync(directory, cancellationToken).ConfigureAwait(false);
        completed++;
        progress?.Report(new AdvancedSftpProgress(
            completed,
            totalItems,
            completedBytes,
            summary.TotalFileBytes,
            directory.Path));
    }

    public async Task DownloadDirectoryZipAsync(
        ISftpSession sftp,
        RemoteFile directory,
        string zipPath,
        IProgress<AdvancedSftpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequirePro();
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        var tree = await BuildTreeAsync(sftp, directory, cancellationToken).ConfigureAwait(false);
        var summary = Summary(tree);
        if (summary.TotalFileBytes > MaxDownloadBytes)
            throw new InvalidOperationException(
                $"This folder contains more than {MaxDownloadBytes / (1024 * 1024 * 1024)} GiB of files. Download a smaller folder or individual files instead.");

        var zipDirectory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrWhiteSpace(zipDirectory)) Directory.CreateDirectory(zipDirectory);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        var files = tree.Where(static node => !node.File.IsDirectory && !node.File.IsSymlink).ToArray();
        var directories = tree.Where(static node => node.File.IsDirectory).ToArray();
        var completed = 0;
        long completedBytes = 0;
        var temporary = zipPath + ".part";

        try
        {
            await using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);

            // Preserve empty directories. Symlinks are skipped instead of followed, preventing
            // a link from silently pulling data from outside the selected tree into the archive.
            foreach (var node in directories)
            {
                var relative = RelativePath(directory.Path, node.File.Path).TrimEnd('/') + "/";
                if (relative.Length > 1) archive.CreateEntry(relative);
            }

            foreach (var node in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await sftp.DownloadAsync(node.File.Path, temporary, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var entry = archive.CreateEntry(RelativePath(directory.Path, node.File.Path), CompressionLevel.Optimal);
                    await using var source = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
                    await using var target = entry.Open();
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    TryDelete(temporary);
                }

                completed++;
                completedBytes += Math.Max(0, node.File.Size);
                progress?.Report(new AdvancedSftpProgress(
                    completed,
                    files.Length,
                    completedBytes,
                    summary.TotalFileBytes,
                    node.File.Path));
            }
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }
    }

    private async Task<IReadOnlyList<TreeNode>> BuildTreeAsync(
        ISftpSession sftp,
        RemoteFile directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sftp);
        ArgumentNullException.ThrowIfNull(directory);
        if (!directory.IsDirectory || directory.IsSymlink)
            throw new ArgumentException("Advanced directory operations require a real directory, not a file or symbolic link.", nameof(directory));

        var result = new List<TreeNode>();
        var pending = new Stack<(RemoteFile Directory, int Depth)>();
        pending.Push((directory, 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = pending.Pop();
            if (depth >= MaxDepth)
                throw new InvalidOperationException($"Folder traversal exceeds the safety limit of {MaxDepth} levels.");

            var children = await sftp.ListAsync(current.Path, cancellationToken).ConfigureAwait(false);
            foreach (var child in children)
            {
                if (child.Name is "." or "..") continue;
                if (result.Count >= MaxEntries)
                    throw new InvalidOperationException($"Folder traversal exceeds the safety limit of {MaxEntries:N0} entries.");

                var childDepth = depth + 1;
                result.Add(new TreeNode(child, childDepth));
                if (child.IsDirectory && !child.IsSymlink)
                    pending.Push((child, childDepth));
            }
        }

        return result;
    }

    private static AdvancedSftpSummary Summary(IReadOnlyList<TreeNode> tree)
    {
        var files = 0;
        var directories = 0;
        var symlinks = 0;
        long bytes = 0;
        foreach (var node in tree)
        {
            if (node.File.IsSymlink) symlinks++;
            else if (node.File.IsDirectory) directories++;
            else
            {
                files++;
                bytes = checked(bytes + Math.Max(0, node.File.Size));
            }
        }
        return new AdvancedSftpSummary(files, directories, symlinks, bytes);
    }

    private static string RelativePath(string root, string path)
    {
        var prefix = root.EndsWith('/') ? root : root + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("The remote server returned an entry outside the selected directory tree.");
        var relative = path[prefix.Length..].Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0 || relative.Split('/').Any(static part => part is "" or "." or ".."))
            throw new InvalidOperationException("The remote server returned an unsafe path while building the archive.");
        return relative;
    }

    private void RequirePro()
    {
        if (!entitlements.Has(PremiumFeature.AdvancedSftp))
            throw new InvalidOperationException("Advanced SFTP workflows require MeowSSH Pro.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record TreeNode(RemoteFile File, int Depth);
}
