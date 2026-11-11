using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowSSH.Core.Licensing;

public enum ManagedDerpNodeKind
{
    Client = 0,
    Server = 1,
}

/// <param name="Grant">A grant bound to this phone's own client node.</param>
/// <param name="NodePublic">The node being registered.</param>
/// <param name="Kind">Client or server.</param>
/// <param name="Label">Optional name shown for the node.</param>
/// <param name="AllowedClientNodes">
/// Servers only: the server's allowed-client list. MeowSSHAPI lets any of them
/// on another account use the managed relay to reach this server, billed to
/// this account (agents/DERP_QUOTA.md, "Guests on another account").
/// </param>
public sealed record ManagedDerpRegistrationRequest(
    SignedEntitlementGrant Grant,
    string NodePublic,
    ManagedDerpNodeKind Kind,
    string? Label = null,
    IReadOnlyList<string>? AllowedClientNodes = null);

public interface IManagedDerpRegistrationService
{
    Task RegisterClientAsync(string clientNodePublic, CancellationToken cancellationToken = default);
    /// <param name="clientNodePublic">This phone's client node, the grant's binding and the guests' billing node.</param>
    /// <param name="serverNodePublic">The server node being registered.</param>
    /// <param name="label">Optional name shown for the server.</param>
    /// <param name="cancellationToken">Cancels the registration.</param>
    /// <param name="allowedClientNodes">
    /// The server's allowed-client list: those on another MeowSSH account may
    /// reach it over the managed relay, on this account's allowance.
    /// </param>
    Task RegisterServerAsync(
        string clientNodePublic,
        string serverNodePublic,
        string? label = null,
        IReadOnlyList<string>? allowedClientNodes = null,
        CancellationToken cancellationToken = default);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ManagedDerpRegistrationRequest))]
internal sealed partial class ManagedDerpJsonContext : JsonSerializerContext
{
}

public sealed class ManagedDerpRegistrationService(
    IManagedDerpGrantProvider grants,
    HttpClient httpClient,
    LicensingApiOptions options) : IManagedDerpRegistrationService
{
    public Task RegisterClientAsync(string clientNodePublic, CancellationToken cancellationToken = default) =>
        RegisterAsync(clientNodePublic, clientNodePublic, ManagedDerpNodeKind.Client, null, null, cancellationToken);

    public Task RegisterServerAsync(
        string clientNodePublic,
        string serverNodePublic,
        string? label = null,
        IReadOnlyList<string>? allowedClientNodes = null,
        CancellationToken cancellationToken = default) =>
        RegisterAsync(clientNodePublic, serverNodePublic, ManagedDerpNodeKind.Server, label, allowedClientNodes, cancellationToken);

    private async Task RegisterAsync(
        string clientNodePublic,
        string nodePublic,
        ManagedDerpNodeKind kind,
        string? label,
        IReadOnlyList<string>? allowedClientNodes,
        CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("The MeowSSH licensing API is not configured.");

        var grant = await grants.GetNodeBoundGrantAsync(clientNodePublic, cancellationToken).ConfigureAwait(false);
        var request = new ManagedDerpRegistrationRequest(grant, nodePublic, kind, label,
            allowedClientNodes is { Count: > 0 } ? allowedClientNodes : null);
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(options.BaseUri!, "v1/derp/nodes/register"),
            request,
            ManagedDerpJsonContext.Default.ManagedDerpRegistrationRequest,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SecurityException("The managed relay service denied node registration.");
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new SecurityException("The Tailcat node is already registered to another managed relay identity.");
        response.EnsureSuccessStatusCode();
    }
}
