using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Ssh;

/// <summary>
/// The Go agent sends os.FileMode, not st_mode. Regression tests for symlinks
/// reading as plain files on every real server (see MeowshellSftpSession.PosixMode);
/// the end-to-end proof is the sftp.browse and sftp.recursive E2E scenarios.
/// </summary>
public class AgentFileModeTests
{
    // Values as Go's uint32(fs.FileMode) produces them.
    private const uint GoDir = 1u << 31, GoSymlink = 1u << 27, GoSetuid = 1u << 23, GoSticky = 1u << 20;

    [Theory]
    [InlineData(GoSymlink | 0x1FF, 0xA1FF)]          // lrwxrwxrwx
    [InlineData(GoDir | 0x1ED, 0x41ED)]              // drwxr-xr-x
    [InlineData(GoDir | GoSticky | 0x1FF, 0x43FF)]   // drwxrwxrwt (/tmp)
    [InlineData(GoSetuid | 0x1ED, 0x89ED)]           // -rwsr-xr-x
    public void GoFileModeBecomesStMode(uint agentMode, uint expected) =>
        Assert.Equal(expected, MeowshellSftpSession.PosixMode(agentMode));

    [Theory]
    [InlineData(0x1A4u)]     // bare permissions: a Go regular file, or a server with no type bits
    [InlineData(0xA1FFu)]    // already st_mode
    [InlineData(0x81A4u)]
    public void StModeAndBarePermissionsPassThrough(uint mode) =>
        Assert.Equal(mode, MeowshellSftpSession.PosixMode(mode));

    [Fact]
    public void ALinkFromTheAgentIsASymlinkAndNotADirectory()
    {
        var file = new RemoteFile("link", "/x/link", 8, MeowshellSftpSession.PosixMode(GoSymlink | 0x1FF), DateTimeOffset.UnixEpoch, false);
        Assert.True(file.IsSymlink);
        Assert.Equal("rwxrwxrwx", file.PermissionString);
    }
}
