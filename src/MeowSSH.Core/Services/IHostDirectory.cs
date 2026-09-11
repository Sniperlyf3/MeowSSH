using MeowSSH.Core.Model;

namespace MeowSSH.Core.Services;

/// <summary>Reads the saved hosts and their current connection state.</summary>
/// <remarks>
/// Deliberately separate from the vault's secret storage. Listing hosts is the
/// app's most common operation and must never require unsealing a credential,
/// so nothing here can return one.
/// </remarks>
public interface IHostDirectory
{
    ValueTask<IReadOnlyList<HostStatus>> GetHostsAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised when a host's connection state changes.</summary>
    event EventHandler? Changed;
}
