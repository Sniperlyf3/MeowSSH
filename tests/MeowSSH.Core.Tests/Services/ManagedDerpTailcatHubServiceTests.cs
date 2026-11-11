using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class ManagedDerpTailcatHubServiceTests
{
    private const string ClientNode = "nodekey:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ServerNode = "nodekey:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task GeneratedClientIdentityIsRegistered()
    {
        var inner = new FakeHub();
        var registrations = new FakeRegistrations();
        var service = new ManagedDerpTailcatHubService(inner, registrations);

        var generated = await service.GenerateKeyAsync(new TailcatGenerateKeyRequest("phone", Client: true));

        Assert.True(generated.Client);
        Assert.Equal(ClientNode, generated.Value);
        Assert.Equal([ClientNode], registrations.Clients);
        Assert.Empty(registrations.Servers);
    }

    [Fact]
    public async Task GeneratedServerIdentityRegistersDefaultClientAndServer()
    {
        var inner = new FakeHub();
        var registrations = new FakeRegistrations();
        var service = new ManagedDerpTailcatHubService(inner, registrations);

        var generated = await service.GenerateKeyAsync(new TailcatGenerateKeyRequest("home-server", Client: false));

        Assert.False(generated.Client);
        Assert.Equal("tc-test-server", generated.Value);
        Assert.Equal([ClientNode], registrations.Clients);
        var server = Assert.Single(registrations.Servers);
        Assert.Equal(ClientNode, server.ClientNode);
        Assert.Equal(ServerNode, server.ServerNode);
        Assert.Equal("home-server", server.Label);
    }

    private const string Guest = "nodekey:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string OtherGuest = "nodekey:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private static TailcatServeRequest Serve(string allowed) =>
        new(TimeSpan.FromMinutes(5), allowed, EnableShell: true, AuthorizedSshKeys: null, EnableFiles: false, SharedFolder: null, FileMode: "ro", EnableExitNode: false);

    [Fact]
    public async Task ASharedServerRegistersItsAllowedClientsAsGuests()
    {
        var registrations = new FakeRegistrations();
        var service = new ManagedDerpTailcatHubService(new FakeHub(), registrations);

        // Separators as people type them, a duplicate, and a saved key name the
        // API could never match.
        await service.StartServerAsync(Serve($"{OtherGuest}, {Guest}\n{Guest} my-laptop"));
        await WaitForAsync(() => registrations.Servers.Count == 1);

        Assert.Equal([Guest, OtherGuest], registrations.ServerGuests.Single());
    }

    [Fact]
    public async Task ChangingTheAllowedClientsRegistersTheServerAgain()
    {
        var registrations = new FakeRegistrations();
        var service = new ManagedDerpTailcatHubService(new FakeHub(), registrations);

        await service.StartServerAsync(Serve(Guest));
        await WaitForAsync(() => registrations.Servers.Count == 1);
        // Same list again: nothing to tell the API.
        await service.StartServerAsync(Serve(Guest));
        await Task.Delay(100);
        Assert.Single(registrations.Servers);
        // Guest removed: it must lose its access, so the API hears about it.
        await service.StartServerAsync(Serve(OtherGuest));
        await WaitForAsync(() => registrations.Servers.Count == 2);

        Assert.Equal([OtherGuest], registrations.ServerGuests[1]);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task RegistrationFailureDoesNotDiscardGeneratedIdentity()
    {
        var inner = new FakeHub();
        var registrations = new FakeRegistrations { Fail = true };
        var service = new ManagedDerpTailcatHubService(inner, registrations);

        var generated = await service.GenerateKeyAsync(new TailcatGenerateKeyRequest("phone", Client: true));

        Assert.Equal(ClientNode, generated.Value);
        Assert.Contains(service.Snapshot.RecentLogLines, line =>
            line.Contains("direct and custom relay paths remain available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulClientRegistrationIsCachedForProcessLifetime()
    {
        var inner = new FakeHub();
        var registrations = new FakeRegistrations();
        var service = new ManagedDerpTailcatHubService(inner, registrations);

        Assert.Equal(ClientNode, await service.GetClientPublicKeyAsync());
        Assert.Equal(ClientNode, await service.GetClientPublicKeyAsync());

        Assert.Equal([ClientNode], registrations.Clients);
    }

    [Fact]
    public async Task ForwardStillStartsWhenManagedRegistrationFails()
    {
        var inner = new FakeHub();
        var registrations = new FakeRegistrations { Fail = true };
        var service = new ManagedDerpTailcatHubService(inner, registrations);

        var forward = await service.StartForwardAsync(new TailcatForwardRequest("tc-peer", ["8080:80"]));

        Assert.Equal("tc-peer", forward.Address);
        Assert.Equal(1, inner.ForwardStarts);
    }

    private sealed class FakeRegistrations : IManagedDerpRegistrationService
    {
        public bool Fail { get; init; }
        public List<string> Clients { get; } = [];
        public List<(string ClientNode, string ServerNode, string? Label)> Servers { get; } = [];

        public Task RegisterClientAsync(string clientNodePublic, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new InvalidOperationException("licensing API unavailable");
            Clients.Add(clientNodePublic);
            return Task.CompletedTask;
        }

        public List<IReadOnlyList<string>> ServerGuests { get; } = [];

        public Task RegisterServerAsync(
            string clientNodePublic,
            string serverNodePublic,
            string? label = null,
            IReadOnlyList<string>? allowedClientNodes = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new InvalidOperationException("licensing API unavailable");
            Servers.Add((clientNodePublic, serverNodePublic, label));
            ServerGuests.Add(allowedClientNodes ?? []);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHub : ITailcatHubService
    {
        private readonly List<TailcatForwardSnapshot> _forwards = [];
        private TailcatServerSnapshot? _server;

        public int ForwardStarts { get; private set; }
        public TailcatHubSnapshot Snapshot => new(_server, null, [.. _forwards], []);
        public event EventHandler? Changed;

        public Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _server = new TailcatServerSnapshot(
                "tc-test-server",
                DateTimeOffset.UtcNow.AddHours(1),
                request.EnableShell,
                request.EnableFiles,
                request.EnableExitNode,
                request.AllowAnyClient,
                request.UseTailcatCredentialForShell,
                request.SharedFolder,
                request.FileMode,
                request.ServeTargets ?? []);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task StopServerAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _server = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopSocksAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<TailcatForwardSnapshot> StartForwardAsync(
            TailcatForwardRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForwardStarts++;
            var snapshot = new TailcatForwardSnapshot(
                Guid.NewGuid(),
                request.Address,
                request.Mappings,
                ["127.0.0.1:18080"],
                request.ClientKey,
                request.Udp);
            _forwards.Add(snapshot);
            return Task.FromResult(snapshot);
        }

        public Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _forwards.RemoveAll(item => item.Id == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["client-default"]);

        public Task<TailcatGeneratedKey> GenerateKeyAsync(
            TailcatGenerateKeyRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TailcatGeneratedKey(
                request.Name,
                request.Client,
                request.Client ? ClientNode : "tc-test-server"));
        }

        public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ClientNode);
        }

        public Task<string> ResolveAddressAsync(
            string address,
            string? derpMapUrl = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(address);

        public Task<TailcatAddressDetails> InspectAddressAsync(
            string address,
            string? derpMapUrl = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TailcatAddressDetails(
                address,
                ServerNode,
                null,
                true,
                1,
                []));
        }

        public Task<TailcatDiagnosticResult> DiagnoseAsync(
            string address,
            bool waitForDirect,
            string? derpMapUrl = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(
            string address,
            string path,
            string? derpMapUrl = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TailcatRemoteFile>>([]);

        public Task UploadAsync(
            string localPath,
            string address,
            string remotePath,
            bool recursive = false,
            string? derpMapUrl = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DownloadAsync(
            string address,
            string remotePath,
            string localPath,
            bool recursive = false,
            string? derpMapUrl = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
