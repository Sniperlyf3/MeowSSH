using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Tests;

public sealed class ManagedDerpUsageServiceTests
{
    private const string ClientNode = "nodekey:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UsageQueryUsesNodeBoundGrantAndReturnsAuthoritativeCounters()
    {
        var grants = new FakeGrantProvider();
        var expected = new ManagedDerpQuotaState(
            ClientNode,
            EntitlementTier.ProCloud,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            125,
            75,
            200,
            1024,
            false);
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(expected, WebJson), Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler);
        var service = CreateService(grants, http);

        var actual = await service.GetCurrentAsync(ClientNode);

        Assert.Equal(ClientNode, grants.LastClientNode);
        Assert.Equal("https://licensing.example/v1/derp/usage/current", handler.RequestUri?.ToString());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("payload", body.RootElement.GetProperty("grant").GetProperty("payloadBase64").GetString());
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AuthorizationFailureFailsClosed()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler);
        var service = CreateService(new FakeGrantProvider(), http);

        await Assert.ThrowsAsync<SecurityException>(() => service.GetCurrentAsync(ClientNode));
    }

    private static ManagedDerpUsageService CreateService(IManagedDerpGrantProvider grants, HttpClient http) =>
        new(
            grants,
            http,
            new LicensingApiOptions(
                new Uri("https://licensing.example/"),
                "public-key",
                "dev.sniperlyf3.meowssh"));

    private sealed class FakeGrantProvider : IManagedDerpGrantProvider
    {
        public string? LastClientNode { get; private set; }

        public Task<SignedEntitlementGrant> GetNodeBoundGrantAsync(
            string clientNodePublic,
            CancellationToken cancellationToken = default)
        {
            LastClientNode = clientNodePublic;
            return Task.FromResult(new SignedEntitlementGrant("payload", "signature"));
        }
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
