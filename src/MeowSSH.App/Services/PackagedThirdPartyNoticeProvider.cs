using MeowSSH.UI.Services;

namespace MeowSSH.App.Services;

internal sealed class PackagedThirdPartyNoticeProvider : IThirdPartyNoticeProvider
{
    public async ValueTask<string> GetNoticesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await FileSystem.Current.OpenAppPackageFileAsync("third_party_notices.txt");
        using var reader = new StreamReader(stream);
        var notices = await reader.ReadToEndAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(notices))
            throw new InvalidOperationException("The bundled third-party notices are empty.");

        return notices;
    }
}
