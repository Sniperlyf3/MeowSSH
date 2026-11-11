using System.Net;
using System.Net.Sockets;
using System.Text;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using Xunit;

namespace MeowSSH.E2E.Scenarios;

/// <summary>
/// Tailcat on the device: the phone serves a shell and files over Tailcat,
/// and the same app connects to the address it published, as a second phone
/// would.
/// </summary>
/// <remarks>
/// The server runs on a loopback relay (TS_DEBUG_TAILCAT_LOCAL_DERP with
/// TAILCAT_DERPMAP_URL=none), set for the server process only, the way
/// Meowshell's own E2E suite does it. The client only ever dials the address
/// the server just published, which is exactly the case that mode is for (see
/// Meowshell's CLAUDE.md on the DERP trap). A run must never reach the public
/// relay: "tailcat Ping: context deadline exceeded" means something did.
/// </remarks>
public static class TailcatScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        new("tailcat.serve-shell-and-files", PhoneServesShellAndFilesOverTailcat),
        new("tailcat.temporary-share.revoke-and-expire", TemporaryShareWorksUntilRevokedOrExpired),
        new("tailcat.workspace.forwards-and-socks", WorkspaceStartsForwardsAndSocksAndSurvivesARestart),
    ];

    private static readonly SemaphoreSlim HermeticEnvironment = new(1, 1);

    /// <summary>Starts the hub's server with the loopback relay in its environment only.</summary>
    private static async Task<string> StartHermeticServerAsync(ITailcatHubService hub, TailcatServeRequest request)
    {
        await HermeticAsync(() => hub.StartServerAsync(request));
        return hub.Snapshot.Server?.Address ?? throw new InvalidOperationException("The server started but published no address.");
    }

    /// <summary>
    /// Runs <paramref name="startServer"/> with the loopback relay in the
    /// environment, so only the server process it starts inherits it.
    /// </summary>
    private static async Task<T> HermeticAsync<T>(Func<Task<T>> startServer)
    {
        await HermeticEnvironment.WaitAsync();
        var previousLocal = Environment.GetEnvironmentVariable("TS_DEBUG_TAILCAT_LOCAL_DERP");
        var previousMap = Environment.GetEnvironmentVariable("TAILCAT_DERPMAP_URL");
        try
        {
            // Inherited by the server process started inside startServer, then
            // put back before any client process starts.
            Environment.SetEnvironmentVariable("TS_DEBUG_TAILCAT_LOCAL_DERP", "1");
            Environment.SetEnvironmentVariable("TAILCAT_DERPMAP_URL", "none");
            return await startServer();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TS_DEBUG_TAILCAT_LOCAL_DERP", previousLocal);
            Environment.SetEnvironmentVariable("TAILCAT_DERPMAP_URL", previousMap);
            HermeticEnvironment.Release();
        }
    }

    private static async Task HermeticAsync(Func<Task> startServer) =>
        await HermeticAsync(async () => { await startServer(); return true; });

    private static async Task PhoneServesShellAndFilesOverTailcat(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var hub = app.Get<ITailcatHubService>();
        var shared = Directory.CreateDirectory(Path.Combine(app.DataDirectory, "shared")).FullName;
        await File.WriteAllTextAsync(Path.Combine(shared, "hello.txt"), "from the phone");

        // The identity the Keys screen shows the user, and the one they would
        // paste into the other phone's allowed-clients list. Every client
        // operation below has to present this same identity to get in.
        var clientKey = await hub.GetClientPublicKeyAsync();
        Assert.StartsWith("nodekey:", clientKey, StringComparison.Ordinal);
        Assert.Equal(clientKey, await hub.GetClientPublicKeyAsync());

        // 1. Files, and a shell guarded by the Tailcat allow list alone.
        var address = await StartHermeticServerAsync(hub, new TailcatServeRequest(
            Lifetime: TimeSpan.FromMinutes(10),
            AllowedClientKeys: clientKey,
            EnableShell: true,
            AuthorizedSshKeys: null,
            EnableFiles: true,
            SharedFolder: shared,
            FileMode: "rw",
            EnableExitNode: false,
            UseTailcatCredentialForShell: true));
        c.Log("serving files at " + address);
        try
        {
            var listed = await hub.ListRemoteFilesAsync(address, "/");
            Assert.Contains(listed, f => f.Name == "hello.txt");
            var downloaded = Path.Combine(app.DataDirectory, "hello-back.txt");
            await hub.DownloadAsync(address, "/hello.txt", downloaded);
            Assert.Equal("from the phone", await File.ReadAllTextAsync(downloaded));
            var upload = Path.Combine(app.DataDirectory, "up.txt");
            await File.WriteAllTextAsync(upload, "to the phone");
            await hub.UploadAsync(upload, address, "/up.txt");
            Assert.Equal("to the phone", await File.ReadAllTextAsync(Path.Combine(shared, "up.txt")));

            // A saved host on the Tailcat transport, with no SSH key at all:
            // the allow-listed identity is the credential.
            var host = await app.AddHostAsync("this phone over tailcat", address, 22, null, h => h with { Transport = SshTransport.Tailcat, Username = "e2e" });
            await RunShellOverTailcatAsync(app, host, shared);
        }
        catch
        {
            foreach (var line in hub.Snapshot.RecentLogLines) c.Log("server: " + line);
            throw;
        }
        finally
        {
            await hub.StopServerAsync();
        }
        Assert.Null(hub.Snapshot.Server);

        // 2. A shell that also requires an authorized SSH key.
        address = await StartHermeticServerAsync(hub, new TailcatServeRequest(
            Lifetime: TimeSpan.FromMinutes(10),
            AllowedClientKeys: clientKey,
            EnableShell: true,
            AuthorizedSshKeys: "ssh-ed25519 " + c.Server.ClientPublicKeyBody + " e2e",
            EnableFiles: false,
            SharedFolder: null,
            FileMode: "ro",
            EnableExitNode: false));
        c.Log("serving a key-authenticated shell at " + address);
        try
        {
            var key = await app.AddPrivateKeyAsync("e2e", c.Server.ClientPrivateKey);
            var keyed = await app.AddHostAsync("this phone, key auth", address, 22, key, h => h with { Transport = SshTransport.Tailcat });
            await RunShellOverTailcatAsync(app, keyed, shared);

            // Without the key, the same address and identity are refused.
            var keyless = await app.AddHostAsync("this phone, no key", address, 22, null, h => h with { Transport = SshTransport.Tailcat, Username = "e2e" });
            await Assert.ThrowsAsync<SshException>(() => app.ConnectAsync(keyless));
        }
        catch
        {
            foreach (var line in hub.Snapshot.RecentLogLines) c.Log("server: " + line);
            throw;
        }
        finally
        {
            await hub.StopServerAsync();
        }
    }

    private static async Task RunShellOverTailcatAsync(E2EApp app, HostRecord host, string shared)
    {
        var asked = app.HostKeyPrompts.Count;
        await using var connection = await app.ConnectAsync(host);
        // The address is the capability: there is no host key to ask about.
        Assert.Equal(asked, app.HostKeyPrompts.Count);
        await using var terminal = new E2ETerminal(await connection.OpenTerminalAsync(80, 24));
        Assert.Equal("42", await terminal.CheckAsync("echo $((6*7))", TimeSpan.FromSeconds(60)));
        Assert.Equal("from the phone", await terminal.CheckAsync($"cat '{shared}/hello.txt'"));
    }

    /// <summary>
    /// A temporary share hands out one port, to named clients, for a bounded
    /// time, under a brand-new identity, and stops working the moment it is
    /// revoked or expires -- the process is gone, not just a flag.
    /// </summary>
    private static async Task TemporaryShareWorksUntilRevokedOrExpired(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var hub = app.Get<ITailcatHubService>();
        var shares = app.Get<ITailcatTemporaryShareService>();
        await using var service = PortService.Start("db");
        var port = service.Port.ToString();
        var clientKey = await hub.GetClientPublicKeyAsync();

        // A share narrows how long an address works, never who may use it.
        await Assert.ThrowsAsync<ArgumentException>(() => shares.CreateAsync(new("no one", [port], " ", TimeSpan.FromMinutes(5))));
        await Assert.ThrowsAsync<ArgumentException>(() => shares.CreateAsync(new("nothing", [" "], clientKey, TimeSpan.FromMinutes(5))));
        Assert.Null(hub.Snapshot.Server);

        var first = await HermeticAsync(() => shares.CreateAsync(new("database", [port], clientKey, TimeSpan.FromMinutes(10))));
        c.Log("temporary share at " + first.Address);
        try
        {
            Assert.Equal(first.Id, shares.Active?.Id);
            var server = hub.Snapshot.Server!;
            // Only the port: no shell, files or exit node behind this address.
            Assert.False(server.ShellEnabled || server.FilesEnabled || server.ExitNodeEnabled || server.AllowsAnyClient);
            await Assert.ThrowsAsync<InvalidOperationException>(() => shares.CreateAsync(new("second", [port], clientKey, TimeSpan.FromMinutes(5))));

            var forward = await hub.StartForwardAsync(new TailcatForwardRequest(first.Address, ["0:" + port]));
            var local = forward.BoundAddresses.Single();
            Assert.Equal("db ping", await PortService.AskAsync(local, "ping", TimeSpan.FromSeconds(60)));
            await Assert.ThrowsAnyAsync<Exception>(() => hub.ListRemoteFilesAsync(first.Address, "/"));

            await shares.RevokeAsync();
            Assert.Null(shares.Active);
            Assert.Null(hub.Snapshot.Server);
            var revoked = (await shares.GetHistoryAsync()).Single(s => s.Id == first.Id);
            Assert.Equal(TailcatTemporaryShareEndReason.Revoked, revoked.EndReason);
            // The forward is still listening on this phone, but nothing is
            // serving the address any more.
            Assert.Null(await PortService.TryAskAsync(local, "ping", TimeSpan.FromSeconds(20)));
            await hub.StopForwardAsync(forward.Id);
        }
        catch
        {
            foreach (var line in hub.Snapshot.RecentLogLines) c.Log("tailcat: " + line);
            throw;
        }
        finally
        {
            if (shares.Active is not null) await shares.RevokeAsync();
        }

        // Sharing again never resurrects an address someone was already handed,
        // and a share left alone ends on its own.
        var second = await HermeticAsync(() => shares.CreateAsync(new("short", [port], clientKey, TimeSpan.FromSeconds(8))));
        Assert.NotEqual(first.Address, second.Address);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (shares.Active is not null && DateTime.UtcNow < deadline) await Task.Delay(500);
        Assert.Null(shares.Active);
        var expired = (await shares.GetHistoryAsync()).Single(s => s.Id == second.Id);
        Assert.Equal(TailcatTemporaryShareEndReason.Expired, expired.EndReason);

        // Pro only.
        var free = await c.StartAppAsync(EntitlementTier.Free);
        await Assert.ThrowsAsync<InvalidOperationException>(() => free.Get<ITailcatTemporaryShareService>()
            .CreateAsync(new("free", [port], clientKey, TimeSpan.FromMinutes(5))));
    }

    /// <summary>
    /// A workspace is a saved bundle -- an address, its forwards and a SOCKS
    /// proxy -- that starts and stops as one, and is still there after the app
    /// restarts.
    /// </summary>
    private static async Task WorkspaceStartsForwardsAndSocksAndSurvivesARestart(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var hub = app.Get<ITailcatHubService>();
        await using var service = PortService.Start("ws");
        var port = service.Port.ToString();
        var clientKey = await hub.GetClientPublicKeyAsync();

        // The other end: a second phone serving the one port to this one. Not
        // this phone's own hub, which its restart below would stop.
        var other = await c.StartAppAsync();
        var otherHub = other.Get<ITailcatHubService>();
        var address = await StartHermeticServerAsync(otherHub, new TailcatServeRequest(
            Lifetime: TimeSpan.FromMinutes(10),
            AllowedClientKeys: clientKey,
            EnableShell: false,
            AuthorizedSshKeys: null,
            EnableFiles: false,
            SharedFolder: null,
            FileMode: "ro",
            EnableExitNode: false,
            ServeTargets: [port]));
        try
        {
            var workspace = TailcatWorkspace.Create("db box") with
            {
                Address = address,
                Forwards = [new TailcatWorkspaceForward(["0:" + port])],
            };
            await app.Get<ITailcatWorkspaceService>().SaveAsync(workspace);

            app = await c.RestartAsync(app);
            hub = app.Get<ITailcatHubService>();
            var workspaces = app.Get<ITailcatWorkspaceService>();
            var saved = Assert.Single(await workspaces.GetAllAsync());
            Assert.Equal((workspace.Id, address), (saved.Id, saved.Address));
            // Same install, same identity: the allow list still admits it.
            Assert.Equal(clientKey, await hub.GetClientPublicKeyAsync());

            await workspaces.StartAsync(saved.Id);
            try
            {
                var active = workspaces.Active!;
                Assert.True(active.OwnsSocks);
                var forward = Assert.Single(hub.Snapshot.Forwards, f => active.ForwardIds.Contains(f.Id));
                var local = forward.BoundAddresses.Single();
                Assert.Equal("ws via-forward", await PortService.AskAsync(local, "via-forward", TimeSpan.FromSeconds(60)));

                var socks = hub.Snapshot.Socks!.ListenAddress;
                Assert.Equal("ws via-socks", await PortService.AskThroughSocksAsync(socks, address, service.Port, "via-socks", TimeSpan.FromSeconds(60)));

                await Assert.ThrowsAsync<InvalidOperationException>(() => workspaces.StartAsync(saved.Id));
                await Assert.ThrowsAsync<InvalidOperationException>(() => workspaces.DeleteAsync(saved.Id));
            }
            finally
            {
                await workspaces.StopAsync();
            }

            Assert.Null(workspaces.Active);
            Assert.Null(hub.Snapshot.Socks);
            Assert.Empty(hub.Snapshot.Forwards);
            await workspaces.DeleteAsync(saved.Id);
            Assert.Empty(await workspaces.GetAllAsync());

            var free = await c.StartAppAsync(EntitlementTier.Free);
            await Assert.ThrowsAsync<InvalidOperationException>(() => free.Get<ITailcatWorkspaceService>().SaveAsync(workspace));
        }
        catch
        {
            foreach (var line in hub.Snapshot.RecentLogLines) c.Log("tailcat: " + line);
            foreach (var line in otherHub.Snapshot.RecentLogLines) c.Log("server: " + line);
            throw;
        }
        finally
        {
            await otherHub.StopServerAsync();
        }
    }

    /// <summary>
    /// A local service for a share to expose: answers each line with its name
    /// and the line, so a reply proves which service, over which path.
    /// </summary>
    private sealed class PortService : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private PortService(string name)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var line = await new StreamReader(stream).ReadLineAsync(_stop.Token);
                            var reply = Encoding.UTF8.GetBytes(name + " " + line + "\n");
                            await stream.WriteAsync(reply, _stop.Token);
                        }
                    });
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static PortService Start(string name) => new(name);

        /// <summary>Sends a line to host:port, retrying until a reply comes or the time runs out.</summary>
        public static async Task<string> AskAsync(string endpoint, string line, TimeSpan timeout) =>
            await TryAskAsync(endpoint, line, timeout)
            ?? throw new TimeoutException($"No reply from {endpoint} within {timeout.TotalSeconds:0}s.");

        /// <summary>Null when nothing answered in time: a refused, reset or silently closed connection.</summary>
        public static async Task<string?> TryAskAsync(string endpoint, string line, TimeSpan timeout)
        {
            var (host, port) = Split(endpoint);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var client = new TcpClient();
                    await client.ConnectAsync(host, port, cts.Token);
                    var reply = await ExchangeAsync(client.GetStream(), line, cts.Token);
                    if (reply is not null) return reply;
                }
                catch (Exception e) when (e is SocketException or IOException or OperationCanceledException) { }
                await Task.Delay(500);
            }
            return null;
        }

        /// <summary>The same exchange through a SOCKS5 proxy that dials Tailcat addresses by name.</summary>
        public static async Task<string> AskThroughSocksAsync(string proxy, string address, int port, string line, TimeSpan timeout)
        {
            var (host, proxyPort) = Split(proxy);
            var deadline = DateTime.UtcNow + timeout;
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var client = new TcpClient();
                    await client.ConnectAsync(host, proxyPort, cts.Token);
                    var stream = client.GetStream();
                    await stream.WriteAsync(new byte[] { 5, 1, 0 }, cts.Token);
                    var greeting = await ReadExactlyAsync(stream, 2, cts.Token);
                    if (greeting[1] != 0) throw new IOException($"SOCKS proxy wants auth method {greeting[1]}.");
                    var name = Encoding.ASCII.GetBytes(address);
                    var connect = new List<byte> { 5, 1, 0, 3, (byte)name.Length };
                    connect.AddRange(name);
                    connect.Add((byte)(port >> 8));
                    connect.Add((byte)port);
                    await stream.WriteAsync(connect.ToArray(), cts.Token);
                    var head = await ReadExactlyAsync(stream, 4, cts.Token);
                    if (head[1] != 0) throw new IOException($"SOCKS CONNECT failed with reply {head[1]}.");
                    var rest = head[3] switch { 1 => 4, 4 => 16, 3 => (await ReadExactlyAsync(stream, 1, cts.Token))[0], _ => 0 };
                    await ReadExactlyAsync(stream, rest + 2, cts.Token);
                    var reply = await ExchangeAsync(stream, line, cts.Token);
                    if (reply is not null) return reply;
                }
                catch (Exception e) when (e is SocketException or IOException or OperationCanceledException) { last = e; }
                await Task.Delay(500);
            }
            throw new TimeoutException($"No reply through SOCKS {proxy} within {timeout.TotalSeconds:0}s.", last);
        }

        private static async Task<string?> ExchangeAsync(Stream stream, string line, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);
            return await new StreamReader(stream).ReadLineAsync(cancellationToken);
        }

        private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return buffer;
        }

        private static (string Host, int Port) Split(string endpoint)
        {
            var colon = endpoint.LastIndexOf(':');
            return (endpoint[..colon].Trim('[', ']'), int.Parse(endpoint[(colon + 1)..]));
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
        }
    }
}
