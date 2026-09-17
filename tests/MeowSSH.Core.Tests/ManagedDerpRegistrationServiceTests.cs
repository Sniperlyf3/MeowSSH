using System.Net;
using System.Security;
using System.Text.Json;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Tests;

public sealed class ManagedDerpRegistrationServiceTests
{
    private const string ClientNode = "nodekey:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ServerNode = "nodekey:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task ClientRegistrationUsesNodeBoundGrantAndClientKind()
    {
        var grants = new FakeGrantProvider();
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var service = CreateService(grants, http);

        await service.RegisterClientAsync(ClientNode);

        Assert.Equal(ClientNode, grants.LastClientNode);
        Assert.Equal("https://licensing.example/v1/derp/nodes/register", handler.RequestUri?.ToString());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(ClientNode, body.RootElement.GetProperty("nodePublic").GetString());
        Assert.Equal((int)ManagedDerpNodeKind.Client, body.RootElement.GetProperty("kind").GetInt32());
        Assert.Equal("payload", body.RootElement.GetProperty("grant").GetProperty("payloadBase64").GetString());
    }

    [Fact]
    public async Task ServerRegistrationUsesClientGrantAndRegistersServerIdentity()
    {
        var grants = new FakeGrantProvider();
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var service = CreateService(grants, http);

        await service.RegisterServerAsync(ClientNode, ServerNode, "phone server");

        Assert.Equal(ClientNode, grants.LastClientNode);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(ServerNode, body.RootElement.GetProperty("nodePublic").GetString());
        Assert.Equal((int)ManagedDerpNodeKind.Server, body.RootElement.GetProperty("kind").GetInt32());
        Assert.Equal("phone server", body.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public async Task OwnershipConflictFailsClosed()
    {
        var handler = new RecordingHandler(HttpStatusCode.Conflict);
        using var http = new HttpClient(handler);
        var service = CreateService(new FakeGrantProvider(), http);

        await Assert.ThrowsAsync<SecurityException>(() => service.RegisterClientAsync(ClientNode));
    }

    private static ManagedDerpRegistrationService CreateService(
        IManagedDerpGrantProvider grants,
        HttpClient http) =>
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

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
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
            return new HttpResponseMessage(statusCode);
        }
    }
}
