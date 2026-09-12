using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Ssh;

public class RemoteFileTests
{
    private static RemoteFile File(long size, uint mode, bool isDirectory = false) =>
        new("x", "/x", size, mode, DateTimeOffset.UnixEpoch, isDirectory);

    [Theory]
    [InlineData(0x81EDu, "rwxr-xr-x")]   // 0755, an executable script
    [InlineData(0x8180u, "rw-------")]   // 0600, a private log
    [InlineData(0x81A4u, "rw-r--r--")]   // 0644
    [InlineData(0x41EDu, "rwxr-xr-x")]   // a directory; type bits must not shift the string
    [InlineData(0x8000u, "---------")]   // no permissions at all
    public void PermissionsRenderTheWayLsPrintsThem(uint mode, string expected)
    {
        Assert.Equal(expected, File(0, mode).PermissionString);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(402, "402 B")]
    [InlineData(999, "999 B")]
    [InlineData(1000, "1.0 kB")]
    [InlineData(3771, "3.8 kB")]
    [InlineData(18_442_240, "18.4 MB")]
    [InlineData(1_205_000_000, "1.2 GB")]
    [InlineData(2_500_000_000_000, "2.5 TB")]
    public void SizesUseOneDecimalAtEveryScaleAboveBytes(long size, string expected)
    {
        // Consistent precision matters more than brevity in a list: "18 MB"
        // beside "1.2 GB" reads as two different formats.
        Assert.Equal(expected, File(size, 0x8000).DisplaySize);
    }

    [Fact]
    public void ADirectoryHasNoSizeToShow()
    {
        // The number would be the size of the directory entry, which users read
        // as the size of its contents.
        Assert.Equal("", File(4096, 0x41ED, isDirectory: true).DisplaySize);
    }

    [Fact]
    public void ASymlinkHasNoSizeToShowEither()
    {
        // The number is the length of the target path, and "0 B" beside a link
        // reads as an empty file.
        Assert.Equal("", File(31, 0xA1FF).DisplaySize);
    }

    [Theory]
    [InlineData(0xA1FFu, true)]    // symlink
    [InlineData(0x81A4u, false)]   // regular file
    [InlineData(0x41EDu, false)]   // directory
    public void SymlinksAreIdentifiedFromTheirTypeBits(uint mode, bool expected)
    {
        Assert.Equal(expected, File(0, mode).IsSymlink);
    }

    [Theory]
    [InlineData(".bashrc", true)]
    [InlineData("..", true)]
    [InlineData("start.sh", false)]
    public void DotfilesAreHidden(string name, bool expected)
    {
        var file = new RemoteFile(name, "/" + name, 0, 0x81A4, DateTimeOffset.UnixEpoch, false);
        Assert.Equal(expected, file.IsHidden);
    }
}

public class RemotePathTests
{
    [Theory]
    [InlineData("/home/deploy", "file.txt", "/home/deploy/file.txt")]
    [InlineData("/", "etc", "/etc")]
    [InlineData("/home/deploy/", "file.txt", "/home/deploy/file.txt")]
    public void JoinBuildsPosixPathsRegardlessOfTheLocalSeparator(string dir, string name, string expected)
    {
        // Why this exists rather than Path.Combine: on Windows that would build
        // a backslash path for a POSIX server, and the bug would be invisible to
        // anyone developing on Linux.
        Assert.Equal(expected, RemotePath.Join(dir, name));
    }

    [Theory]
    [InlineData("/home/deploy/file.txt", "/home/deploy")]
    [InlineData("/home/deploy", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/", null)]
    [InlineData("/home/deploy/", "/home")]
    public void ParentOfWalksUpAndStopsAtTheRoot(string path, string? expected)
    {
        Assert.Equal(expected, RemotePath.ParentOf(path));
    }

    [Theory]
    [InlineData("/home/deploy/file.txt", "file.txt")]
    [InlineData("/home/deploy/", "deploy")]
    [InlineData("/", "/")]
    public void NameOfTakesTheFinalSegment(string path, string expected)
    {
        Assert.Equal(expected, RemotePath.NameOf(path));
    }

    [Fact]
    public void BreadcrumbsStartAtTheRootAndNameEverySegment()
    {
        var crumbs = RemotePath.Breadcrumbs("/home/deploy/releases");

        Assert.Equal(["/", "home", "deploy", "releases"], crumbs.Select(c => c.Label));
        Assert.Equal(["/", "/home", "/home/deploy", "/home/deploy/releases"], crumbs.Select(c => c.Path));
    }

    [Fact]
    public void BreadcrumbsForTheRootAreJustTheRoot()
    {
        Assert.Equal([("/", "/")], RemotePath.Breadcrumbs("/"));
    }
}
