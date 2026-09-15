namespace MeowSSH.UI.Services;

/// <summary>Opens a URI using the platform rather than navigating the embedded app WebView.</summary>
public interface IExternalUriLauncher
{
    Task<bool> OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

/// <summary>Browser/test-host implementation; tests only need to prove the forward was created.</summary>
public sealed class NoOpExternalUriLauncher : IExternalUriLauncher
{
    public Task<bool> OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}
