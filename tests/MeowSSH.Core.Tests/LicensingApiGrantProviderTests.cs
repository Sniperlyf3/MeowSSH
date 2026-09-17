using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Tests;

public sealed class LicensingApiGrantProviderTests
{
    private const string ClientNode = "nodekey:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherNode = "nodekey:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
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
    public async Task UnconfiguredProviderFailsClosedWithoutCallingStoreOrIntegrity()
    {
        var store = new FakeStore(throwIfCalled: true);
        var integrity = new FakeIntegrity(throwIfCalled: true);
        using var http = new HttpClient(new StaticHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var provider = new LicensingApiGrantProvider(
            store,
            integrity,
            http,
            new LicensingApiOptions(null, string.Empty, "dev.sniperlyf3.meowssh"));

        var entitlement = await provider.RefreshAsync();

        Assert.Equal(EntitlementTier.Free, entitlement.Tier);
    }

    [Fact]
    public async Task MissingIntegrityTokenFailsClosedBeforeCallingBackend()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        using var http = new HttpClient(new ThrowingHandler());
        var publicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        var provider = new LicensingApiGrantProvider(
            new FakeStore(),
            new FakeIntegrity(token: null),
            http,
            new LicensingApiOptions(new Uri("https://licensing.example/"), publicKey, "dev.sniperlyf3.meowssh"),
            new FixedTimeProvider(now));

        var entitlement = await provider.RefreshAsync();

        Assert.Equal(EntitlementTier.Free, entitlement.Tier);
    }

    [Fact]
    public async Task NodeBoundFreeGrantUsesFreeIntegrityFlow()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var claims = new EntitlementGrantClaims(
            "grant-free",
            "dev.sniperlyf3.meowssh",
            EntitlementTier.Free,
            now,
            now.AddHours(1),
            ClientNode);
        var handler = new RecordingHandler(CreateSignedResponse(signer, claims));
        using var http = new HttpClient(handler);
        var provider = CreateProvider(signer, http, now, new FakeStore(purchases: []));

        var grant = await provider.GetNodeBoundGrantAsync(ClientNode);

        Assert.False(string.IsNullOrWhiteSpace(grant.PayloadBase64));
        Assert.Equal("https://licensing.example/v1/entitlements/free/verify", handler.RequestUri?.ToString());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(ClientNode, body.RootElement.GetProperty("clientNodePublic").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("integrityNonce").GetString()));
    }

    [Fact]
    public async Task NodeBoundPaidGrantUsesPaidIntegrityFlowAndIncludesNode()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var claims = new EntitlementGrantClaims(
            "grant-pro",
            "dev.sniperlyf3.meowssh",
            EntitlementTier.Pro,
            now,
            now.AddHours(1),
            ClientNode);
        var handler = new RecordingHandler(CreateSignedResponse(signer, claims));
        using var http = new HttpClient(handler);
        var provider = CreateProvider(signer, http, now);

        await provider.GetNodeBoundGrantAsync(ClientNode);

        Assert.Equal("https://licensing.example/v1/entitlements/google-play/verify", handler.RequestUri?.ToString());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(ClientNode, body.RootElement.GetProperty("clientNodePublic").GetString());
        Assert.Single(body.RootElement.GetProperty("purchases").EnumerateArray());
    }

    [Fact]
    public async Task NodeBoundGrantRejectsGrantForAnotherNode()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var claims = new EntitlementGrantClaims(
            "grant-wrong-node",
            "dev.sniperlyf3.meowssh",
            EntitlementTier.Free,
            now,
            now.AddHours(1),
            OtherNode);
        using var http = new HttpClient(new StaticHandler(CreateSignedResponse(signer, claims)));
        var provider = CreateProvider(signer, http, now, new FakeStore(purchases: []));

        await Assert.ThrowsAsync<SecurityException>(() => provider.GetNodeBoundGrantAsync(ClientNode));
    }

    [Fact]
    public void IntegrityNonceIsStableAcrossPurchaseOrderingAndChangesWhenRequestChanges()
    {
        var first = new[]
        {
            new LicensingVerificationPurchase("b", "token-2"),
            new LicensingVerificationPurchase("a", "token-1"),
        };
        var reversed = first.Reverse();

        var nonce1 = LicensingApiGrantProvider.CreateIntegrityNonce("dev.sniperlyf3.meowssh", first, "request-1");
        var nonce2 = LicensingApiGrantProvider.CreateIntegrityNonce("dev.sniperlyf3.meowssh", reversed, "request-1");
        var nonce3 = LicensingApiGrantProvider.CreateIntegrityNonce("dev.sniperlyf3.meowssh", first, "request-2");
        var nodeNonce = LicensingApiGrantProvider.CreateIntegrityNonce("dev.sniperlyf3.meowssh", first, "request-1", ClientNode);

        Assert.Equal(nonce1, nonce2);
        Assert.NotEqual(nonce1, nonce3);
        Assert.NotEqual(nonce1, nodeNonce);
        Assert.False(nonce1.Contains('+', StringComparison.Ordinal));
        Assert.False(nonce1.Contains('/', StringComparison.Ordinal));
        Assert.False(nonce1.Contains('=', StringComparison.Ordinal));
    }

    private static LicensingApiGrantProvider CreateProvider(
        ECDsa signer,
        HttpClient http,
        DateTimeOffset now,
        IStorePurchaseService? store = null)
    {
        var publicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        return new LicensingApiGrantProvider(
            store ?? new FakeStore(),
            new FakeIntegrity(),
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

    private sealed class FakeStore(bool throwIfCalled = false, IReadOnlyList<StorePurchase>? purchases = null) : IStorePurchaseService
    {
        public Task<IReadOnlyList<StoreProduct>> GetProductsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoreProduct>>([]);

        public Task<IReadOnlyList<StorePurchase>> GetPurchasesAsync(CancellationToken cancellationToken = default)
        {
            if (throwIfCalled) throw new InvalidOperationException("Store should not be called.");
            return Task.FromResult(purchases ?? (IReadOnlyList<StorePurchase>)[
                new StorePurchase(MeowSshProducts.ProLifetime, "purchase-token", false, false),
            ]);
        }

        public Task<StorePurchase?> PurchaseAsync(
            string productId,
            string? basePlanId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<StorePurchase?>(null);
    }

    private sealed class FakeIntegrity(string? token = "integrity-token", bool throwIfCalled = false) : IPlayIntegrityService
    {
        public Task<string?> RequestTokenAsync(string nonce, CancellationToken cancellationToken = default)
        {
            if (throwIfCalled) throw new InvalidOperationException("Integrity should not be called.");
            Assert.False(string.IsNullOrWhiteSpace(nonce));
            return Task.FromResult(token);
        }
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Backend should not be called.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
