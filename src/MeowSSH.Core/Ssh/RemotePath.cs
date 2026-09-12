namespace MeowSSH.Core.Ssh;

/// <summary>
/// POSIX path arithmetic for paths on a remote host.
/// </summary>
/// <remarks>
/// Deliberately not <c>System.IO.Path</c>. Those methods use the separator of
/// the machine the code is running on, so on Windows they would build backslash
/// paths for a POSIX server — and the bug would be invisible to anyone
/// developing on Linux.
/// </remarks>
public static class RemotePath
{
    public const string Root = "/";

    /// <summary>Appends <paramref name="name"/> to <paramref name="directory"/>.</summary>
    public static string Join(string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(name);
        if (directory.Length == 0) return name;
        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    /// <summary>The containing directory, or null when already at the root.</summary>
    public static string? ParentOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        if (trimmed is Root or "") return null;
        var cut = trimmed.LastIndexOf('/');
        return cut switch
        {
            < 0 => null,
            0 => Root,
            _ => trimmed[..cut],
        };
    }

    /// <summary>The final segment of a path.</summary>
    public static string NameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        if (trimmed is Root or "") return Root;
        var cut = trimmed.LastIndexOf('/');
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>
    /// Every directory from the root down to <paramref name="path"/>, as
    /// (label, path) pairs for a breadcrumb trail.
    /// </summary>
    public static IReadOnlyList<(string Label, string Path)> Breadcrumbs(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var crumbs = new List<(string, string)> { (Root, Root) };
        var walked = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walked += "/" + segment;
            crumbs.Add((segment, walked));
        }
        return crumbs;
    }
}
