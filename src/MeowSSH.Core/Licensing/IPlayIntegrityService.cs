namespace MeowSSH.Core.Licensing;

public interface IPlayIntegrityService
{
    /// <summary>
    /// Requests a Google Play Integrity token bound to the supplied URL-safe Base64 nonce.
    /// The token is opaque to the client and must be decoded and verified by the licensing backend.
    /// </summary>
    Task<string?> RequestTokenAsync(string nonce, CancellationToken cancellationToken = default);
}

public sealed class UnavailablePlayIntegrityService : IPlayIntegrityService
{
    public Task<string?> RequestTokenAsync(string nonce, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        return Task.FromResult<string?>(null);
    }
}
