using System.Globalization;

namespace MeowSSH.Core.Ssh;

/// <summary>One entry in a remote directory.</summary>
/// <param name="Name">The entry's own name, with no path.</param>
/// <param name="Path">Absolute path on the server.</param>
/// <param name="Size">Size in bytes. Meaningless for a directory.</param>
/// <param name="Mode">Unix mode bits, as SFTP reported them.</param>
/// <param name="ModifiedAt">Last modification time.</param>
/// <param name="IsDirectory">Whether this can be navigated into.</param>
public sealed record RemoteFile(
    string Name,
    string Path,
    long Size,
    uint Mode,
    DateTimeOffset ModifiedAt,
    bool IsDirectory)
{
    private const uint TypeMask = 0xF000;
    private const uint SymlinkType = 0xA000;

    /// <summary>
    /// Whether this is a symbolic link.
    /// </summary>
    /// <remarks>
    /// Worth surfacing rather than silently following: a link is the usual reason
    /// a file appears to be in two places, and the usual reason a delete does
    /// less than the user expected.
    /// </remarks>
    public bool IsSymlink => (Mode & TypeMask) == SymlinkType;

    /// <summary>Whether the name begins with a dot, as dotfiles do.</summary>
    public bool IsHidden => Name.StartsWith('.');

    /// <summary>Permissions in the <c>rwxr-xr-x</c> form <c>ls -l</c> prints.</summary>
    public string PermissionString
    {
        get
        {
            Span<char> chars = stackalloc char[9];
            const string flags = "rwx";
            for (var i = 0; i < 9; i++)
                chars[i] = (Mode & (1u << (8 - i))) != 0 ? flags[i % 3] : '-';
            return new string(chars);
        }
    }

    /// <summary>Size rendered for a phone screen, e.g. <c>1.4 MB</c>.</summary>
    /// <remarks>
    /// Decimal units, because that is what file managers and storage labels use;
    /// showing 1.4 MiB next to a 2 GB phone would be comparing two scales.
    /// </remarks>
    public string DisplaySize
    {
        get
        {
            // A symlink's size is the length of the path it points at, which is
            // not information anyone reads a file list for -- and "0 B" beside a
            // link reads as an empty file.
            if (IsDirectory || IsSymlink) return "";
            if (Size < 1000) return $"{Size} B";

            // One decimal at every scale above bytes. Varying the precision by
            // magnitude reads as inconsistent in a list -- "18 MB" next to
            // "1.2 GB" looks like two different formats -- and the decimal is
            // what makes two similarly sized files distinguishable.
            double value = Size;
            foreach (var unit in new[] { "kB", "MB", "GB", "TB" })
            {
                value /= 1000;
                if (value < 1000 || unit == "TB")
                    return string.Create(CultureInfo.InvariantCulture, $"{value:F1} {unit}");
            }
            return $"{Size} B";
        }
    }
}

/// <summary>How far a transfer has got.</summary>
/// <param name="BytesTransferred">Bytes moved so far.</param>
/// <param name="TotalBytes">
/// Total expected, or null when the engine cannot know it — an upload reports
/// only progress, so the caller supplies the total from the local file.
/// </param>
public sealed record TransferProgress(long BytesTransferred, long? TotalBytes)
{
    /// <summary>Fraction complete from 0 to 1, or null when the total is unknown.</summary>
    public double? Fraction => TotalBytes is > 0 ? Math.Min(1d, (double)BytesTransferred / TotalBytes.Value) : null;
}
