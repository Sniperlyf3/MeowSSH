namespace MeowSSH.UI.Services;

public interface IThirdPartyNoticeProvider
{
    ValueTask<string> GetNoticesAsync(CancellationToken cancellationToken = default);
}
