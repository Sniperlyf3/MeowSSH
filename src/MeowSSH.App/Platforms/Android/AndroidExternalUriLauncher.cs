using MeowSSH.UI.Services;

namespace MeowSSH.App;

public sealed class AndroidExternalUriLauncher : IExternalUriLauncher
{
    public async Task<bool> OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return await Launcher.Default.OpenAsync(uri).ConfigureAwait(false);
    }
}
