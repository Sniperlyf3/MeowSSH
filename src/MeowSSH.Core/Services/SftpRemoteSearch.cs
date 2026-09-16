using MeowSSH.Core.Licensing;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

public sealed record SftpSearchResult(RemoteFile File, string RelativePath);

public interface ISftpRemoteSearchService
{
    Task<IReadOnlyList<SftpSearchResult>> SearchAsync(
        ISftpSession sftp,
        RemoteFile root,
        string query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded, on-demand recursive remote search. It never follows symlink directories and keeps no index.
/// </summary>
public sealed class SftpRemoteSearchService(IEntitlementService entitlements) : ISftpRemoteSearchService
{
    internal const int MaxDepth = 32;
    internal const int MaxVisitedEntries = 5_000;
    internal const int MaxResults = 200;
    internal const int MaxQueryLength = 256;

    public async Task<IReadOnlyList<SftpSearchResult>> SearchAsync(
        ISftpSession sftp,
        RemoteFile root,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (!entitlements.Has(PremiumFeature.AdvancedSftp))
            throw new InvalidOperationException("Remote SFTP search requires MeowSSH Pro.");
        ArgumentNullException.ThrowIfNull(sftp);
        ArgumentNullException.ThrowIfNull(root);
        if (!root.IsDirectory || root.IsSymlink)
            throw new ArgumentException("Remote search requires a real directory root.", nameof(root));

        var needle = query?.Trim() ?? string.Empty;
        if (needle.Length < 2)
            throw new ArgumentException("Enter at least 2 characters to search.", nameof(query));
        if (needle.Length > MaxQueryLength)
            throw new ArgumentException($"Search text must be {MaxQueryLength} characters or fewer.", nameof(query));

        var results = new List<SftpSearchResult>();
        var pending = new Stack<(RemoteFile Directory, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth >= MaxDepth)
                throw new InvalidOperationException($"Remote search exceeds the safety limit of {MaxDepth} levels.");

            var children = await sftp.ListAsync(directory.Path, cancellationToken).ConfigureAwait(false);
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Name is "." or "..") continue;
                visited++;
                if (visited > MaxVisitedEntries)
                    throw new InvalidOperationException($"Remote search exceeds the safety limit of {MaxVisitedEntries:N0} entries.");

                if (Matches(child, needle))
                {
                    results.Add(new SftpSearchResult(child, RelativePath(root.Path, child.Path)));
                    if (results.Count >= MaxResults)
                        return results;
                }

                if (child.IsDirectory && !child.IsSymlink)
                    pending.Push((child, depth + 1));
            }
        }

        return results;
    }

    private static bool Matches(RemoteFile file, string query) =>
        file.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || file.Path.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string RelativePath(string root, string path)
    {
        if (root == "/") return path.TrimStart('/');
        var prefix = root.EndsWith('/') ? root : root + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }
}
