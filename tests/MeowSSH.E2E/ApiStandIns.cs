using System.Text;
using System.Text.Json;
using MeowSSH.Core.Licensing;

namespace MeowSSH.E2E;

/// <summary>
/// The Play Store on the device, for runs against a real API: what the user
/// has bought. Tokens are the self-describing ones MeowSSHAPI's
/// E2EGooglePlayStandIn decodes; change the two together.
/// </summary>
public sealed class E2EPlayStore : IStorePurchaseService
{
    public const string ProLifetime = "meowssh_pro_lifetime";
    public const string ProCloud = "meowssh_pro_cloud";
    public const string Team = "meowssh_team";

    private readonly List<StorePurchase> _purchases = [];

    /// <summary>The purchases a user on <paramref name="tier"/> would have.</summary>
    public static E2EPlayStore For(EntitlementTier tier)
    {
        var store = new E2EPlayStore();
        switch (tier)
        {
            case EntitlementTier.Pro: store.Buy(ProLifetime); break;
            case EntitlementTier.ProCloud: store.Buy(ProCloud); break;
            case EntitlementTier.Team: store.Buy(Team); break;
        }
        return store;
    }

    /// <param name="valid">False: Google no longer vouches for it (expired, cancelled, never paid).</param>
    /// <param name="voided">True: refunded or charged back.</param>
    public StorePurchase Buy(string productId, bool valid = true, bool voided = false)
    {
        var kind = productId == ProLifetime ? "OneTime" : "Subscription";
        var token = "e2e-purchase." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            productId,
            kind,
            valid,
            acknowledged = false,
            voided,
        }));
        var purchase = new StorePurchase(productId, token, IsAcknowledged: false, IsPending: false);
        lock (_purchases) _purchases.Add(purchase);
        return purchase;
    }

    public void Clear() { lock (_purchases) _purchases.Clear(); }

    public Task<IReadOnlyList<StoreProduct>> GetProductsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoreProduct>>([]);

    public Task<IReadOnlyList<StorePurchase>> GetPurchasesAsync(CancellationToken cancellationToken = default)
    {
        lock (_purchases) return Task.FromResult<IReadOnlyList<StorePurchase>>([.. _purchases]);
    }

    public Task<StorePurchase?> PurchaseAsync(string productId, string? basePlanId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<StorePurchase?>(Buy(productId));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Play Integrity on the device: a verdict for exactly the nonce the app asked
/// about, so the API's nonce binding is exercised for real.
/// </summary>
public sealed class E2EPlayIntegrity : IPlayIntegrityService
{
    /// <summary>What Google says about the app; anything but LICENSED is refused by the API.</summary>
    public string AppLicensingVerdict { get; set; } = "LICENSED";

    public Task<string?> RequestTokenAsync(string nonce, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>("e2e-integrity." + E2EPlayStore.Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            nonce,
            packageName = E2EApi.PackageName,
            certificateSha256Digest = E2EApi.AppCertificateDigest,
            appLicensingVerdict = AppLicensingVerdict,
        }))));
}
