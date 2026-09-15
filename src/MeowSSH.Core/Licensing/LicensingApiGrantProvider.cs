using System.Net.Http.Json;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    string IntegrityToken);
public sealed record SignedEntitlementGrant(string PayloadBase64, string SignatureBase64);
public sealed record EntitlementGrantClaims(
    string GrantId,
    string PackageName,
    EntitlementTier Tier,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ValidUntilUtc);

public sealed class LicensingApiGrantProvider : IEntitlementGrantProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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

    private async Task<EntitlementSnapshot> VerifyPurchasesAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!_options.IsConfigured) return EntitlementSnapshot.Free(now);

        var purchases = (await _store.GetPurchasesAsync(cancellationToken).ConfigureAwait(false))
            .Where(static purchase => !purchase.IsPending && !string.IsNullOrWhiteSpace(purchase.PurchaseToken))
            .Select(static purchase => new LicensingVerificationPurchase(purchase.ProductId, purchase.PurchaseToken))
            .ToArray();
        if (purchases.Length == 0) return EntitlementSnapshot.Free(now);

        var requestId = Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var integrityNonce = CreateIntegrityNonce(_options.PackageName, purchases, requestId);
        var integrityToken = await _integrity.RequestTokenAsync(integrityNonce, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(integrityToken)) return EntitlementSnapshot.Free(now);

        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(_options.BaseUri!, "v1/entitlements/google-play/verify"),
            new LicensingVerificationRequest(_options.PackageName, purchases, requestId, integrityNonce, integrityToken),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var grant = await response.Content.ReadFromJsonAsync<SignedEntitlementGrant>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SecurityException("The licensing server returned an empty entitlement grant.");

        return VerifyGrant(grant, now);
    }

    public static string CreateIntegrityNonce(
        string packageName,
        IEnumerable<LicensingVerificationPurchase> purchases,
        string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentNullException.ThrowIfNull(purchases);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var builder = new StringBuilder();
        AppendLengthPrefixed(builder, packageName);
        AppendLengthPrefixed(builder, requestId);
        foreach (var purchase in purchases
                     .OrderBy(static value => value.ProductId, StringComparer.Ordinal)
                     .ThenBy(static value => value.PurchaseToken, StringComparer.Ordinal))
        {
            AppendLengthPrefixed(builder, purchase.ProductId);
            AppendLengthPrefixed(builder, purchase.PurchaseToken);
        }

        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private EntitlementSnapshot VerifyGrant(SignedEntitlementGrant grant, DateTimeOffset now)
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

        var claims = JsonSerializer.Deserialize<EntitlementGrantClaims>(payload, JsonOptions)
            ?? throw new SecurityException("The licensing entitlement payload is invalid.");

        if (!string.Equals(claims.PackageName, _options.PackageName, StringComparison.Ordinal))
            throw new SecurityException("The licensing entitlement was issued for another application.");
        if (claims.IssuedAtUtc > now.AddMinutes(5))
            throw new SecurityException("The licensing entitlement was issued in the future.");
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

    private static void AppendLengthPrefixed(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
