using MeowSSH.Core;

namespace MeowSSH.Core.Tests;

public class BuildInfoTests
{
    [Fact]
    public void ACommitStampedVersionShowsTheShortCommit()
    {
        // What CI produces: SourceRevisionId appended by the SDK after a "+".
        Assert.Equal("0.1.0 (abcdef1)", BuildInfo.Format("0.1.0+abcdef1234567890"));
    }

    [Fact]
    public void AVersionWithNoCommitIsShownAsItIs()
    {
        // A local build has no source revision to stamp.
        Assert.Equal("0.1.0", BuildInfo.Format("0.1.0"));
    }

    [Fact]
    public void AShortRevisionIsDroppedRatherThanTruncatedMisleadingly()
    {
        // Anything under seven characters is not a commit anyone can look up,
        // and half of one shown as if it were is worse than none.
        Assert.Equal("0.1.0", BuildInfo.Format("0.1.0+abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingVersionSaysSoRatherThanRenderingBlank(string? informationalVersion)
    {
        // This line exists to be read out when reporting a bug. An empty one
        // would leave the reader thinking they had read it correctly.
        Assert.Equal("unknown build", BuildInfo.Format(informationalVersion));
    }

    [Fact]
    public void TheRunningBuildReportsSomethingPrintable()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Version));
    }
}
