using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Tests;

public sealed class LicensingApiGrantProviderTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ValidSignedGrantUnlocksServerVerifiedPro()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var claims = new EntitlementGrantClaims("grant-1", "dev.sniperlyf3.meowssh", EntitlementTier.Pro, now, now.AddDays(7));
        var response = CreateSignedResponse(signer, claims);
        using var http = new HttpClient(new StaticHandler(response)) { BaseAddress = new Uri("https://licensing.example/") };
        var provider = CreateProvider(signer, http, now);

        var entitlement = await provider.RefreshAsync();

        Assert.Equal(EntitlementTier.Pro, entitlement.Tier);
        Assert.Equal(EntitlementSource.ServerVerifiedGooglePlay, entitlement.Source);
        Assert.Equal("grant-1", entitlement.GrantId);
    }

    [Fact]
    public async Task TamperedSignatureIsRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var claims = new EntitlementGrantClaims("grant-1", "dev.sniperlyf3.meowssh", EntitlementTier.Pro, now, now.AddDays(7));
        var response = CreateSignedResponse(signer, claims, tamperSignature: true);
        using var http = new HttpClient(new StaticHandler(response)) { BaseAddress = new Uri("https://licensing.example/") };
        var provider = CreateProvider(signer, http, now);

        await Assert.ThrowsAsync<SecurityException>(() => provider.RefreshAsync());
    }

    [Fact]
    public async Task UnconfiguredProviderFailsClosedWithoutCallingStore()
    {
        var store = new FakeStore(throwIfCalled: true);
        using var http = new HttpClient(new StaticHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var provider = new LicensingApiGrantProvider(
            store,
            http,
            new LicensingApiOptions(null, string.Empty, "dev.sniperlyf3.meowssh"));

        var entitlement = await provider.RefreshAsync();

        Assert.Equal(EntitlementTier.Free, entitlement.Tier);
    }

    private static LicensingApiGrantProvider CreateProvider(ECDsa signer, HttpClient http, DateTimeOffset now)
    {
        var publicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        return new LicensingApiGrantProvider(
            new FakeStore(),
            http,
            new LicensingApiOptions(new Uri("https://licensing.example/"), publicKey, "dev.sniperlyf3.meowssh"),
            new FixedTimeProvider(now));
    }

    private static HttpResponseMessage CreateSignedResponse(ECDsa signer, EntitlementGrantClaims claims, bool tamperSignature = false)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims, WebJson);
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256);
        if (tamperSignature) signature[0] ^= 0xff;
        var envelope = new SignedEntitlementGrant(Convert.ToBase64String(payload), Convert.ToBase64String(signature));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope, WebJson), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeStore(bool throwIfCalled = false) : IStorePurchaseService
    {
        public Task<IReadOnlyList<StoreProduct>> GetProductsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoreProduct>>([]);

        public Task<IReadOnlyList<StorePurchase>> GetPurchasesAsync(CancellationToken cancellationToken = default)
        {
            if (throwIfCalled) throw new InvalidOperationException("Store should not be called.");
            return Task.FromResult<IReadOnlyList<StorePurchase>>([
                new StorePurchase(MeowSshProducts.ProLifetime, "purchase-token", false, false),
            ]);
        }

        public Task<StorePurchase?> PurchaseAsync(string productId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StorePurchase?>(null);
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
