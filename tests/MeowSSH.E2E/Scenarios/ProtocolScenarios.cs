using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;
using Xunit;

namespace MeowSSH.E2E.Scenarios;

/// <summary>
/// The non-SSH engines: Mosh, Telnet and the device's own local terminal,
/// each reached through the app's ConnectionEngine dispatch exactly as the
/// host list reaches them. (USB serial needs an adapter on the device and is
/// covered by unit tests only.)
/// </summary>
public static class ProtocolScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        new("mosh.shell", MoshSessionRunsCommands),
        new("telnet.shell", TelnetSessionRunsCommands),
        new("local-terminal.shell", LocalTerminalRunsOnTheDevice),
    ];

    private static async Task MoshSessionRunsCommands(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var key = await app.AddPrivateKeyAsync(c.Server.Username, c.Server.ClientPrivateKey);
        var host = await app.AddHostAsync(c.Server, key, h => h with { Protocol = HostProtocol.Mosh });

        // Bootstraps over SSH (so the host key question is asked as for SSH),
        // then the session itself runs over UDP to mosh-server.
        await using var connection = await app.ConnectAsync(host);
        Assert.Single(app.HostKeyPrompts);
        await using var terminal = new E2ETerminal(await connection.OpenTerminalAsync(80, 24));

        // Mosh repaints a screen rather than streaming bytes, so match on a
        // value only an evaluating shell can print, not on exact framing.
        await terminal.SendAsync("echo MOSH_$((6*7))_OK\n");
        await terminal.WaitForTextAsync("MOSH_42_OK", TimeSpan.FromSeconds(60));
        await terminal.SendAsync("echo $MOSH_E2E_UNSET${SSH_CONNECTION:+VIA_SSHD}\n");
        await terminal.WaitForTextAsync("VIA_SSHD", TimeSpan.FromSeconds(30));
    }

    private static async Task TelnetSessionRunsCommands(E2EContext c)
    {
        var (app, sshHost) = await c.StartWithKeyHostAsync();

        // No telnetd is left running on a test server between runs: start one
        // for this scenario as the unprivileged test user, loopback only.
        var port = 20000 + Random.Shared.Next(20000);
        await using (var ssh = await app.ConnectSshAsync(sshHost))
        {
            var started = await ssh.RunCommandAsync(
                $"busybox --list 2>/dev/null | grep -qx telnetd || {{ echo 'no busybox telnetd on the E2E server (install busybox-static)' >&2; exit 3; }}; " +
                $"nohup busybox telnetd -F -p {port} -b 127.0.0.1 -l /bin/bash >/dev/null 2>&1 & echo $! > /tmp/meow-e2e-telnetd-{port}.pid; sleep 1; " +
                $"kill -0 $(cat /tmp/meow-e2e-telnetd-{port}.pid)", TimeSpan.FromSeconds(30));
            Assert.True(started.Succeeded, $"telnetd did not start: {started.StandardError}");
        }

        try
        {
            var telnet = await app.AddHostAsync("telnet", c.Server.Host, port, null, h => h with { Protocol = HostProtocol.Telnet });
            var hostKeyQuestions = app.HostKeyPrompts.Count;
            await using var connection = await app.ConnectAsync(telnet);
            // Telnet never sends credentials on its own, and has no host key to ask about.
            Assert.Equal(hostKeyQuestions, app.HostKeyPrompts.Count);
            Assert.Equal(0, app.PasswordPrompts);

            await using var terminal = new E2ETerminal(await connection.OpenTerminalAsync(90, 30));
            Assert.Equal("42", await terminal.CheckAsync("echo $((6*7))", TimeSpan.FromSeconds(30)));
            Assert.Equal(c.Server.Username, await terminal.CheckAsync("whoami"));
        }
        finally
        {
            await using var ssh = await app.ConnectSshAsync(sshHost);
            await ssh.RunCommandAsync($"kill $(cat /tmp/meow-e2e-telnetd-{port}.pid); rm -f /tmp/meow-e2e-telnetd-{port}.pid");
        }
    }

    private static async Task LocalTerminalRunsOnTheDevice(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var local = await app.AddHostAsync("this device", "", 0, null, h => h with { Protocol = HostProtocol.Local });

        await using var connection = await app.ConnectAsync(local);
        await using var terminal = new E2ETerminal(await connection.OpenTerminalAsync(80, 24));

        Assert.Equal("42", await terminal.CheckAsync("echo $((6*7))", TimeSpan.FromSeconds(30)));
        // It is this device's shell, not the server's: the app's own files are here.
        var dataDir = await terminal.CheckAsync($"test -d '{app.DataDirectory}' && echo present");
        Assert.Equal("present", dataDir);
    }
}
