using MeowSSH.UI.Services;

namespace MeowSSH.TestHost.Fakes;

public sealed class FakeThirdPartyNoticeProvider : IThirdPartyNoticeProvider
{
    public ValueTask<string> GetNoticesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            "Third-party notices\n\nTailcat — BSD 3-Clause\n\nMeowSSH is independent and is not affiliated with, sponsored by, or endorsed by Tailscale Inc.");
    }
}
