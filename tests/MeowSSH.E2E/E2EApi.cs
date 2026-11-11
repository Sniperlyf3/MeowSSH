using System.Net.Http.Json;
using System.Security.Cryptography;
using MeowSSH.Core.Licensing;

namespace MeowSSH.E2E;

/// <summary>
/// A real, running MeowSSHAPI for the scenarios that need the backend:
/// licensing, the managed relay, cloud backup and sync, heartbeats, teams.
/// </summary>
/// <remarks>
/// MeowSSHAPI is private, so its CI is the one that has both: it builds and
/// starts the API (hosting environment "E2E", Google Play stand-in on, a
/// self-signed TLS certificate), checks out this repository, and runs these
/// scenarios against it. This repository's own CI has no API, and they skip.
///
/// The app only talks HTTPS to the API, so the connection is real TLS, pinned
/// to exactly the certificate the API was started with. Nothing is relaxed for
/// any other certificate.
/// </remarks>
/// <param name="BaseUri">The API root, e.g. https://127.0.0.1:5443/ (10.0.2.2 from the emulator).</param>
/// <param name="CertificateSha256">Hex SHA-256 of the API's DER-encoded TLS certificate.</param>
public sealed record E2EApi(Uri BaseUri, string CertificateSha256)
{
    /// <summary>
    /// The relay's own credential for the API's /internal/derp endpoints
    /// (E2E_API_RELAY_TOKEN), so a scenario can ask exactly what the relay asks
    /// before it carries a pair's traffic. Null when the run did not pass one.
    /// </summary>
    public string? RelayToken { get; init; }

    public const string PackageName = "dev.sniperlyf3.meowssh";

    /// <summary>
    /// The signing-certificate digest the app's stand-in Play Integrity token
    /// claims, and the API is configured to allow (base64url SHA-256 of
    /// "meowssh-e2e"). Not a real certificate: nothing ships signed with it.
    /// </summary>
    public static string AppCertificateDigest { get; } =
        Convert.ToBase64String(SHA256.HashData("meowssh-e2e"u8)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads E2E_API_URL and E2E_API_CERT_SHA256 (case as the source has them); null when either is unset.</summary>
    public static E2EApi? From(Func<string, string?> get)
    {
        var url = get("e2e_api_url");
        var cert = get("e2e_api_cert_sha256");
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(cert)) return null;
        // One without the other would quietly turn every API scenario into a
        // skip, or into a TLS failure that looks like the API being down.
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(cert))
            throw new InvalidOperationException("Set both E2E_API_URL and E2E_API_CERT_SHA256, or neither.");
        var uri = new Uri(url.EndsWith('/') ? url : url + "/");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("E2E_API_URL must be https: the app refuses anything else.");
        var relayToken = get("e2e_api_relay_token");
        return new E2EApi(uri, cert.Replace(":", "", StringComparison.Ordinal).Trim().ToUpperInvariant())
        {
            RelayToken = string.IsNullOrWhiteSpace(relayToken) ? null : relayToken.Trim(),
        };
    }

    /// <summary>An HttpClient that trusts the API's certificate and no other untrusted one.</summary>
    public HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            errors == System.Net.Security.SslPolicyErrors.None ||
            (certificate is not null &&
             string.Equals(Convert.ToHexString(SHA256.HashData(certificate.RawData)), CertificateSha256, StringComparison.Ordinal)),
    });

    /// <summary>The key the app verifies grants with, as a build bakes it in.</summary>
    public async Task<LicensingApiOptions> LicensingOptionsAsync(CancellationToken cancellationToken = default)
    {
        using var http = CreateHttpClient();
        var key = await http.GetFromJsonAsync<PublicKeyResponse>(new Uri(BaseUri, "v1/entitlements/public-key"), cancellationToken)
            ?? throw new InvalidOperationException("The API returned no grant public key.");
        return new LicensingApiOptions(BaseUri, key.SubjectPublicKeyInfoBase64, PackageName);
    }

    /// <summary>
    /// What the managed relay asks before forwarding packets between two nodes,
    /// and what it bills them to (/internal/derp/authorize-pair).
    /// </summary>
    public async Task<RelayPairDecision> AuthorizePairAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        var token = RelayToken ?? throw new InvalidOperationException(
            "This scenario asks the API what the relay asks, and needs E2E_API_RELAY_TOKEN (MeowSSHAPI's run-app-e2e.sh sets it).");
        using var http = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "internal/derp/authorize-pair"))
        {
            Content = JsonContent.Create(new { sourceNodePublic = source, destinationNodePublic = destination }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RelayPairDecision>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned no pair decision.");
    }

    /// <param name="ClientNodePublic">The node the relay bills this pair's traffic to.</param>
    public sealed record RelayPairDecision(bool Allow, string Reason, string? ClientNodePublic);

    private sealed record PublicKeyResponse(string SubjectPublicKeyInfoBase64);
}
