using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Ssh;

public sealed class SshPathStatusTests
{
    [Theory]
    [InlineData(true, "", "Direct")]
    [InlineData(false, "", "Relayed")]
    [InlineData(false, "ci", "Relayed · ci")]
    public void LabelIsConciseAndTruthful(bool direct, string relay, string expected)
    {
        Assert.Equal(expected, new SshPathStatus(direct, relay).Label);
    }
}
