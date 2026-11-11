using System.Net;
using System.Net.Sockets;
using System.Text;
using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using Xunit;

namespace MeowSSH.E2E.Scenarios;

/// <summary>
/// Local, remote and SOCKS forwarding, saved forward profiles, jump hosts and
/// agent forwarding. The "service" behind a forward is the server's own sshd,
/// whose banner proves bytes really crossed the tunnel. Its port as the server
/// sees it is <see cref="E2EServer.Port"/>: true for both runs, since the
/// emulator's 10.0.2.2 maps straight onto the runner's loopback.
/// </summary>
public static class ForwardingScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        new("forward.local", LocalForwardReachesAServiceBehindTheServer),
        new("forward.remote", RemoteForwardReachesAServiceOnTheDevice),
        new("forward.socks", SocksForwardRequiresItsCredentialsAndTunnels),
        new("forward.profile.persist-and-open", SavedProfileSurvivesARestartAndOpens),
        new("jump-host.chain", JumpHostCarriesTheConnection),
        new("agent-forwarding", ForwardedAgentLetsTheServerLogInOnward),
    ];

    private static string ServerSideSshd(E2EContext c) => $"127.0.0.1:{c.Server.Port}";

    private static async Task LocalForwardReachesAServiceBehindTheServer(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await using var ssh = await app.ConnectSshAsync(host);
        await using var forward = await ssh.OpenLocalForwardAsync("127.0.0.1:0", ServerSideSshd(c));
        Assert.Equal(SshForwardKind.Local, forward.Kind);

        Assert.StartsWith("SSH-2.0-", await ReadBannerAsync(ParseEndpoint(forward.BoundAddress)));

        // Stopped means stopped: the port refuses. Then the "await using"
        // disposes it a second time, which used to hang forever.
        await forward.CloseAsync();
        await Assert.ThrowsAnyAsync<SocketException>(() => ReadBannerAsync(ParseEndpoint(forward.BoundAddress)));
    }

    private static async Task RemoteForwardReachesAServiceOnTheDevice(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await using var ssh = await app.ConnectSshAsync(host);

        // A service only the device has: the server can reach it only through the tunnel.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var greeting = "meow-" + Guid.NewGuid().ToString("N")[..8];
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(greeting + "\n"));
            await Task.Delay(500);
        });

        await using var forward = await ssh.OpenRemoteForwardAsync("127.0.0.1:0", $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var remotePort = ParseEndpoint(forward.BoundAddress).Port;
        Assert.True(remotePort > 0, $"no server-side port in '{forward.BoundAddress}'");

        var read = await ssh.RunCommandAsync(
            $"timeout 20 bash -c 'exec 3<>/dev/tcp/127.0.0.1/{remotePort}; head -n1 <&3'", TimeSpan.FromSeconds(30));
        Assert.Equal(greeting, read.StandardOutput.Trim());
        await serve;
    }

    private static async Task SocksForwardRequiresItsCredentialsAndTunnels(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await using var ssh = await app.ConnectSshAsync(host);
        await using var socks = await ssh.OpenSocksForwardAsync("127.0.0.1:0", requireAuth: true);
        Assert.False(string.IsNullOrEmpty(socks.SocksUsername));
        Assert.False(string.IsNullOrEmpty(socks.SocksPassword));
        var proxy = ParseEndpoint(socks.BoundAddress);

        using (var tunnel = await Socks5ConnectAsync(proxy, socks.SocksUsername!, socks.SocksPassword!, "127.0.0.1", c.Server.Port))
            Assert.StartsWith("SSH-2.0-", await ReadLineAsync(tunnel.GetStream()));

        // Anyone else on the device must not be able to use the tunnel.
        await Assert.ThrowsAsync<IOException>(() => Socks5ConnectAsync(proxy, socks.SocksUsername!, "wrong", "127.0.0.1", c.Server.Port));
    }

    private static async Task SavedProfileSurvivesARestartAndOpens(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await app.Get<IPortForwardProfileService>().SaveAsync(new PortForwardProfile(
            Guid.NewGuid(), "server sshd", SshForwardKind.Local, "127.0.0.1:0", ServerSideSshd(c),
            AllowNonLoopbackBind: false, RequireSocksAuth: false, MaxConnections: 4, UseUnixSocket: false, DateTimeOffset.UtcNow));

        app = await c.RestartAsync(app);
        var profile = Assert.Single(await app.Get<IPortForwardProfileService>().GetAllAsync());
        Assert.Equal(ServerSideSshd(c), profile.Destination);

        // Applied the way the Forwards screen applies one.
        await using var ssh = await app.ConnectSshAsync(host);
        await using var forward = await ssh.OpenLocalForwardAsync(profile.ListenAddress, profile.Destination!, profile.AllowNonLoopbackBind, profile.MaxConnections);
        Assert.StartsWith("SSH-2.0-", await ReadBannerAsync(ParseEndpoint(forward.BoundAddress)));
    }

    private static async Task JumpHostCarriesTheConnection(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var key = await app.AddPrivateKeyAsync(c.Server.Username, c.Server.ClientPrivateKey);
        var jump = await app.AddHostAsync(c.Server, key);
        // Addressed as the jump host sees it. From the emulator, 127.0.0.1 is
        // the phone itself, so only a connection routed through the jump can work.
        var target = await app.AddHostAsync("behind jump", "127.0.0.1", c.Server.Port, key, h => h with { JumpHostId = jump.Id });

        await using (var chained = await app.ConnectSshAsync(target))
            Assert.Equal(c.Server.Username, (await chained.RunCommandAsync("whoami")).StandardOutput.Trim());

        // And the route really is through the jump: break only the jump's
        // login and the destination becomes unreachable.
        var badPassword = await app.AddPasswordAsync(c.Server.Username, "not-the-password");
        var brokenJump = await app.AddHostAsync(c.Server, badPassword);
        var unreachable = await app.AddHostAsync("behind broken jump", "127.0.0.1", c.Server.Port, key, h => h with { JumpHostId = brokenJump.Id });
        var ex = await Assert.ThrowsAsync<SshException>(() => app.ConnectAsync(unreachable));
        Assert.Equal(SshFailure.AuthenticationFailed, ex.Failure);
    }

    private static async Task ForwardedAgentLetsTheServerLogInOnward(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var key = await app.AddPrivateKeyAsync(c.Server.Username, c.Server.ClientPrivateKey);
        var forwarding = await app.AddHostAsync(c.Server, key, h => h with { ForwardAgent = true });
        var plain = await app.AddHostAsync(c.Server, key);

        // The server's sshd is its own next hop: logging in again from there
        // with no key on disk works only with the phone's key, via the agent.
        var onward = $"ssh -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o IdentitiesOnly=no -o IdentityFile=/dev/null -p {c.Server.Port} {c.Server.Username}@127.0.0.1 echo onward-ok";

        await using (var ssh = await app.ConnectSshAsync(forwarding))
        {
            var listed = await ssh.RunCommandAsync("ssh-add -L");
            Assert.Equal(0, listed.ExitCode);
            var keyBody = c.Server.ClientPublicKeyBody;
            Assert.Contains(keyBody, listed.StandardOutput, StringComparison.Ordinal);
            Assert.Equal("onward-ok", (await ssh.RunCommandAsync(onward, TimeSpan.FromSeconds(30))).StandardOutput.Trim());
        }

        // Off by default: a host that did not ask for it exposes no agent.
        await using (var ssh = await app.ConnectSshAsync(plain))
            Assert.Equal("", (await ssh.RunCommandAsync("printf %s \"$SSH_AUTH_SOCK\"")).StandardOutput);
    }

    private static IPEndPoint ParseEndpoint(string address)
    {
        var colon = address.LastIndexOf(':');
        var hostPart = address[..colon].Trim('[', ']');
        return new IPEndPoint(hostPart is "localhost" or "" ? IPAddress.Loopback : IPAddress.Parse(hostPart), int.Parse(address[(colon + 1)..]));
    }

    private static async Task<string> ReadBannerAsync(IPEndPoint endpoint)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint);
        return await ReadLineAsync(client.GetStream());
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var line = new StringBuilder();
        var one = new byte[1];
        while (await stream.ReadAsync(one, timeout.Token) == 1 && one[0] != '\n') line.Append((char)one[0]);
        return line.ToString().TrimEnd('\r');
    }

    /// <summary>A SOCKS5 CONNECT with username/password auth (RFC 1928/1929), as a browser would make it.</summary>
    private static async Task<TcpClient> Socks5ConnectAsync(IPEndPoint proxy, string user, string password, string host, int port)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxy);
            var s = client.GetStream();
            await s.WriteAsync(new byte[] { 5, 1, 2 });
            var choice = await ReadExactlyAsync(s, 2);
            if (choice[1] != 2) throw new IOException($"proxy refused username/password auth ({choice[1]})");

            var u = Encoding.UTF8.GetBytes(user);
            var p = Encoding.UTF8.GetBytes(password);
            await s.WriteAsync((byte[])[1, (byte)u.Length, .. u, (byte)p.Length, .. p]);
            if ((await ReadExactlyAsync(s, 2))[1] != 0) throw new IOException("proxy rejected the credentials");

            var h = Encoding.ASCII.GetBytes(host);
            await s.WriteAsync((byte[])[5, 1, 0, 3, (byte)h.Length, .. h, (byte)(port >> 8), (byte)port]);
            var reply = await ReadExactlyAsync(s, 4);
            if (reply[1] != 0) throw new IOException($"proxy CONNECT failed ({reply[1]})");
            var addressLength = reply[3] switch { 1 => 4, 4 => 16, 3 => (await ReadExactlyAsync(s, 1))[0], _ => throw new IOException("bad SOCKS reply") };
            await ReadExactlyAsync(s, addressLength + 2);
            return client;
        }
        catch (Exception ex) when (ex is SocketException or EndOfStreamException)
        {
            client.Dispose();
            throw new IOException("SOCKS connection closed: " + ex.Message, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }
}
