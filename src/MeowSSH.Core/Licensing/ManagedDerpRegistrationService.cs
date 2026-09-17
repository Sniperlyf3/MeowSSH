using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text.Json;

namespace MeowSSH.Core.Licensing;

public enum ManagedDerpNodeKind
{
    Client = 0,
    Server = 1,
}

public sealed record ManagedDerpRegistrationRequest(
    SignedEntitlementGrant Grant,
    string NodePublic,
    ManagedDerpNodeKind Kind,
    string? Label = null);

public interface IManagedDerpRegistrationService
{
    Task RegisterClientAsync(string clientNodePublic, CancellationToken cancellationToken = default);
    Task RegisterServerAsync(
        string clientNodePublic,
        string serverNodePublic,
        string? label = null,
        CancellationToken cancellationToken = default);
}

public sealed class ManagedDerpRegistrationService(
    IManagedDerpGrantProvider grants,
    HttpClient httpClient,
    LicensingApiOptions options) : IManagedDerpRegistrationService
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public Task RegisterClientAsync(string clientNodePublic, CancellationToken cancellationToken = default) =>
        RegisterAsync(clientNodePublic, clientNodePublic, ManagedDerpNodeKind.Client, null, cancellationToken);

    public Task RegisterServerAsync(
        string clientNodePublic,
        string serverNodePublic,
        string? label = null,
        CancellationToken cancellationToken = default) =>
        RegisterAsync(clientNodePublic, serverNodePublic, ManagedDerpNodeKind.Server, label, cancellationToken);

    private async Task RegisterAsync(
        string clientNodePublic,
        string nodePublic,
        ManagedDerpNodeKind kind,
        string? label,
        CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("The MeowSSH licensing API is not configured.");

        var grant = await grants.GetNodeBoundGrantAsync(clientNodePublic, cancellationToken).ConfigureAwait(false);
        var request = new ManagedDerpRegistrationRequest(grant, nodePublic, kind, label);
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(options.BaseUri!, "v1/derp/nodes/register"),
            request,
            WebJson,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SecurityException("The managed relay service denied node registration.");
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new SecurityException("The Tailcat node is already registered to another managed relay identity.");
        response.EnsureSuccessStatusCode();
    }
}
