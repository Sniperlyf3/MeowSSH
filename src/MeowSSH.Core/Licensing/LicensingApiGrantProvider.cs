using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowSSH.Core.Licensing;

public sealed record LicensingApiOptions(
    Uri? BaseUri,
    string PublicKeySubjectPublicKeyInfoBase64,
    string PackageName)
{
    public bool IsConfigured =>
        BaseUri is not null &&
        BaseUri.IsAbsoluteUri &&
        BaseUri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(PublicKeySubjectPublicKeyInfoBase64) &&
        !string.IsNullOrWhiteSpace(PackageName);
}

public sealed record LicensingVerificationPurchase(string ProductId, string PurchaseToken);
public sealed record LicensingVerificationRequest(
    string PackageName,
    IReadOnlyList<LicensingVerificationPurchase> Purchases,
    string RequestId,
    string IntegrityNonce,
    string IntegrityToken,
    string? ClientNodePublic = null);
public sealed record FreeEntitlementVerificationRequest(
    string PackageName,
    string RequestId,
    string IntegrityNonce,
    string IntegrityToken,
    string ClientNodePublic);
public sealed record SignedEntitlementGrant(string PayloadBase64, string SignatureBase64);
public sealed record EntitlementGrantClaims(
    string GrantId,
    string PackageName,
    EntitlementTier Tier,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ValidUntilUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientNodePublic = null);

public interface IManagedDerpGrantProvider
{
    Task<SignedEntitlementGrant> GetNodeBoundGrantAsync(
        string clientNodePublic,
        CancellationToken cancellationToken = default);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LicensingVerificationRequest))]
[JsonSerializable(typeof(FreeEntitlementVerificationRequest))]
[JsonSerializable(typeof(SignedEntitlementGrant))]
[JsonSerializable(typeof(EntitlementGrantClaims))]
internal sealed partial class LicensingApiJsonContext : JsonSerializerContext
{
}

public sealed class LicensingApiGrantProvider : IEntitlementGrantProvider, IManagedDerpGrantProvider
{
    private readonly IStorePurchaseService _store;
    private readonly IPlayIntegrityService _integrity;
    private readonly HttpClient _httpClient;
    private readonly LicensingApiOptions _options;
    private readonly TimeProvider _timeProvider;

    public LicensingApiGrantProvider(
        IStorePurchaseService store,
        IPlayIntegrityService integrity,
        HttpClient httpClient,
        LicensingApiOptions options,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _integrity = integrity ?? throw new ArgumentNullException(nameof(integrity));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        VerifyPurchasesAsync(cancellationToken);

    public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) =>
        VerifyPurchasesAsync(cancellationToken);

    public async Task<SignedEntitlementGrant> GetNodeBoundGrantAsync(
        string clientNodePublic,
        CancellationToken cancellationToken = default)
    {
        var normalizedNode = NormalizeNodePublic(clientNodePublic);
        if (!_options.IsConfigured)
            throw new InvalidOperationException("The MeowSSH licensing API is not configured.");

        var purchases = await GetPurchasesAsync(cancellationToken).ConfigureAwait(false);
        var requestId = Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var integrityNonce = CreateIntegrityNonce(
            _options.PackageName,
            purchases,
            requestId,
            normalizedNode);
        var integrityToken = await _integrity.RequestTokenAsync(integrityNonce, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(integrityToken))
            throw new SecurityException("Google Play Integrity did not return a token for managed relay authorization.");

        HttpResponseMessage response;
        if (purchases.Length == 0)
        {
            var request = new FreeEntitlementVerificationRequest(
                _options.PackageName,
                requestId,
                integrityNonce,
                integrityToken,
                normalizedNode);
            response = await _httpClient.PostAsJsonAsync(
                new Uri(_options.BaseUri!, "v1/entitlements/free/verify"),
                request,
                LicensingApiJsonContext.Default.FreeEntitlementVerificationRequest,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var request = new LicensingVerificationRequest(
                _options.PackageName,
                purchases,
                requestId,
                integrityNonce,
                integrityToken,
                normalizedNode);
            response = await _httpClient.PostAsJsonAsync(
                new Uri(_options.BaseUri!, "v1/entitlements/google-play/verify"),
                request,
                LicensingApiJsonContext.Default.LicensingVerificationRequest,
                cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SecurityException("The licensing service denied managed relay authorization.");
            response.EnsureSuccessStatusCode();

            var grant = await response.Content.ReadFromJsonAsync(
                    LicensingApiJsonContext.Default.SignedEntitlementGrant,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new SecurityException("The licensing server returned an empty entitlement grant.");

            var now = _timeProvider.GetUtcNow();
            var claims = VerifyGrantClaims(grant, now);
            if (claims.ValidUntilUtc <= now)
                throw new SecurityException("The managed relay entitlement grant has expired.");
            if (claims.Tier is < EntitlementTier.Free or > EntitlementTier.Team)
                throw new SecurityException("The managed relay entitlement grant contains an invalid tier.");
            if (!string.Equals(claims.ClientNodePublic, normalizedNode, StringComparison.Ordinal))
                throw new SecurityException("The managed relay entitlement grant is bound to another Tailcat node.");

            return grant;
        }
    }

    private async Task<EntitlementSnapshot> VerifyPurchasesAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!_options.IsConfigured) return EntitlementSnapshot.Free(now);

        var purchases = await GetPurchasesAsync(cancellationToken).ConfigureAwait(false);
        if (purchases.Length == 0) return EntitlementSnapshot.Free(now);

        var requestId = Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var integrityNonce = CreateIntegrityNonce(_options.PackageName, purchases, requestId);
        var integrityToken = await _integrity.RequestTokenAsync(integrityNonce, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(integrityToken)) return EntitlementSnapshot.Free(now);

        var verificationRequest = new LicensingVerificationRequest(
            _options.PackageName,
            purchases,
            requestId,
            integrityNonce,
            integrityToken);

        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(_options.BaseUri!, "v1/entitlements/google-play/verify"),
            verificationRequest,
            LicensingApiJsonContext.Default.LicensingVerificationRequest,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var grant = await response.Content.ReadFromJsonAsync(
                LicensingApiJsonContext.Default.SignedEntitlementGrant,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SecurityException("The licensing server returned an empty entitlement grant.");

        return VerifyGrant(grant, now);
    }

    public static string CreateIntegrityNonce(
        string packageName,
        IEnumerable<LicensingVerificationPurchase> purchases,
        string requestId,
        string? clientNodePublic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentNullException.ThrowIfNull(purchases);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var builder = new StringBuilder();
        AppendLengthPrefixed(builder, packageName);
        AppendLengthPrefixed(builder, requestId);
        if (clientNodePublic is not null)
            AppendLengthPrefixed(builder, NormalizeNodePublic(clientNodePublic));
        foreach (var purchase in purchases
                     .OrderBy(static value => value.ProductId, StringComparer.Ordinal)
                     .ThenBy(static value => value.PurchaseToken, StringComparer.Ordinal))
        {
            AppendLengthPrefixed(builder, purchase.ProductId);
            AppendLengthPrefixed(builder, purchase.PurchaseToken);
        }

        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private async Task<LicensingVerificationPurchase[]> GetPurchasesAsync(CancellationToken cancellationToken) =>
        (await _store.GetPurchasesAsync(cancellationToken).ConfigureAwait(false))
        .Where(static purchase => !purchase.IsPending && !string.IsNullOrWhiteSpace(purchase.PurchaseToken))
        .Select(static purchase => new LicensingVerificationPurchase(purchase.ProductId, purchase.PurchaseToken))
        .ToArray();

    private EntitlementSnapshot VerifyGrant(SignedEntitlementGrant grant, DateTimeOffset now)
    {
        var claims = VerifyGrantClaims(grant, now);
        if (claims.ValidUntilUtc <= now)
            return EntitlementSnapshot.Free(now);
        if (claims.Tier is < EntitlementTier.Pro or > EntitlementTier.Team)
            throw new SecurityException("The licensing entitlement contains an invalid paid tier.");

        return new EntitlementSnapshot(
            claims.Tier,
            EntitlementSource.ServerVerifiedGooglePlay,
            now,
            claims.ValidUntilUtc,
            claims.GrantId);
    }

    private EntitlementGrantClaims VerifyGrantClaims(SignedEntitlementGrant grant, DateTimeOffset now)
    {
        byte[] payload;
        byte[] signature;
        byte[] publicKey;
        try
        {
            payload = Convert.FromBase64String(grant.PayloadBase64);
            signature = Convert.FromBase64String(grant.SignatureBase64);
            publicKey = Convert.FromBase64String(_options.PublicKeySubjectPublicKeyInfoBase64);
        }
        catch (FormatException exception)
        {
            throw new SecurityException("The licensing grant or public key is not valid Base64.", exception);
        }

        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
        if (bytesRead != publicKey.Length || !verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256))
            throw new SecurityException("The licensing entitlement signature is invalid.");

        var claims = JsonSerializer.Deserialize(
                payload,
                LicensingApiJsonContext.Default.EntitlementGrantClaims)
            ?? throw new SecurityException("The licensing entitlement payload is invalid.");

        if (!string.Equals(claims.PackageName, _options.PackageName, StringComparison.Ordinal))
            throw new SecurityException("The licensing entitlement was issued for another application.");
        if (claims.IssuedAtUtc > now.AddMinutes(5))
            throw new SecurityException("The licensing entitlement was issued in the future.");
        return claims;
    }

    private static string NormalizeNodePublic(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (!normalized.StartsWith("nodekey:", StringComparison.Ordinal) ||
            normalized.Length != "nodekey:".Length + 64)
        {
            throw new ArgumentException("A canonical Tailcat node public key is required.", nameof(value));
        }

        foreach (var ch in normalized.AsSpan("nodekey:".Length))
        {
            if (!Uri.IsHexDigit(ch))
                throw new ArgumentException("A canonical Tailcat node public key is required.", nameof(value));
        }
        return normalized;
    }

    private static void AppendLengthPrefixed(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
