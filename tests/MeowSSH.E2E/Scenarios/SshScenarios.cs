using System.Security.Cryptography;
using System.Text;
using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;
using Xunit;

namespace MeowSSH.E2E.Scenarios;

/// <summary>
/// Connecting: every way to authenticate, the host-key decisions, the shell,
/// and what happens when the connection goes away. Each goes through the
/// app's own vault, credential resolver, prompt sheets and engine chain.
/// </summary>
public static class SshScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        new("ssh.private-key.shell", PrivateKeyOpensARealShell),
        new("ssh.host-key.accepted-and-remembered", AcceptedHostKeyIsRemembered),
        new("ssh.host-key.declined", DeclinedHostKeyRefusesAndRecordsNothing),
        new("ssh.host-key.changed", ChangedHostKeyRefusesWithoutAsking),
        new("ssh.password.stored", StoredPasswordAuthenticatesWithoutPrompting),
        new("ssh.password.wrong", WrongStoredPasswordFailsAsAuthentication),
        new("ssh.password.typed", PasswordTypedAtThePromptAuthenticates),
        new("ssh.private-key.passphrase", PassphraseProtectedKeyAuthenticates),
        new("ssh.keystore-key", NonExportableKeystoreKeyAuthenticates),
        new("ssh.credential-username-wins", CredentialUsernameOverridesTheHost),
        new("ssh.pty.size-and-resize", PtyHasTheRequestedSizeAndFollowsResizes),
        new("ssh.exec", ExecReturnsOutputErrorAndExitCode),
        new("ssh.shell.exit", ShellExitIsReported),
        new("ssh.connection-lost.reconnect", DroppedConnectionIsReportedAndReconnects),
    ];

    private static async Task PrivateKeyOpensARealShell(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await using var connection = await app.ConnectAsync(host);
        Assert.True(connection.IsConnected);

        await using var terminal = new E2ETerminal(await connection.OpenTerminalAsync(80, 24));
        // Arithmetic, so the answer can only come from a shell that evaluated it.
        Assert.Equal("42", await terminal.CheckAsync("echo $((6*7))"));
        Assert.Equal(c.Server.Username, await terminal.CheckAsync("whoami"));
    }

    private static async Task AcceptedHostKeyIsRemembered(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await using (await app.ConnectAsync(host)) { }

        // Asked once, and about this server's actual key: a prompt showing some
        // other fingerprint would have the user approve the wrong machine.
        var prompt = Assert.Single(app.HostKeyPrompts);
        Assert.Equal(c.Server.HostKeyFingerprint, prompt.Fingerprint);

        // Refusing now proves the second connect relied on the remembered
        // answer rather than simply not asking.
        app.AcceptUnknownHostKeys = false;
        await using (var again = await app.ConnectAsync(host)) Assert.True(again.IsConnected);
        Assert.Single(app.HostKeyPrompts);
    }

    private static async Task DeclinedHostKeyRefusesAndRecordsNothing(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        app.AcceptUnknownHostKeys = false;

        var ex = await Assert.ThrowsAsync<SshException>(() => app.ConnectAsync(host));
        Assert.Equal(SshFailure.HostKeyUnknown, ex.Failure);
        Assert.Single(app.HostKeyPrompts);

        var recorded = File.Exists(app.KnownHostsPath) ? await File.ReadAllTextAsync(app.KnownHostsPath) : "";
        Assert.DoesNotContain(c.Server.HostPublicKey.Split(' ')[1], recorded, StringComparison.Ordinal);
    }

    private static async Task ChangedHostKeyRefusesWithoutAsking(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();

        // A key of the server's own type that is not the server's: what the
        // user would see after the server was rebuilt, or with someone in between.
        var type = c.Server.HostPublicKey.Split(' ')[0];
        var impostor = type == "ssh-ed25519" ? RandomEd25519PublicKey() : throw new InvalidOperationException($"Unexpected host key type {type}.");
        Directory.CreateDirectory(Path.GetDirectoryName(app.KnownHostsPath)!);
        var hostPattern = c.Server.Port == 22 ? c.Server.Host : $"[{c.Server.Host}]:{c.Server.Port}";
        await File.WriteAllTextAsync(app.KnownHostsPath, $"{hostPattern} {impostor}\n");

        var ex = await Assert.ThrowsAsync<SshException>(() => app.ConnectAsync(host));
        // Changed, not unknown: one is a question for the user, the other a
        // warning that must never be turned into a "trust it?" prompt.
        Assert.Equal(SshFailure.HostKeyChanged, ex.Failure);
        Assert.False(ex.IsRetryable);
        Assert.Empty(app.HostKeyPrompts);
    }

    private static async Task StoredPasswordAuthenticatesWithoutPrompting(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var password = await app.AddPasswordAsync(c.Server.Username, c.Server.Password);
        var host = await app.AddHostAsync(c.Server, password);

        await using var connection = await app.ConnectSshAsync(host);
        Assert.Equal(c.Server.Username, (await connection.RunCommandAsync("whoami")).StandardOutput.Trim());
        Assert.Equal(0, app.PasswordPrompts);
    }

    private static async Task WrongStoredPasswordFailsAsAuthentication(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var password = await app.AddPasswordAsync(c.Server.Username, c.Server.Password + "-wrong");
        var host = await app.AddHostAsync(c.Server, password);

        var ex = await Assert.ThrowsAsync<SshException>(() => app.ConnectAsync(host));
        // Not a network error and not "unknown": the screen tells the user to
        // check their password only if this is classified correctly.
        Assert.Equal(SshFailure.AuthenticationFailed, ex.Failure);
    }

    private static async Task PasswordTypedAtThePromptAuthenticates(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var host = await app.AddHostAsync(c.Server, null, h => h with { Username = c.Server.Username });
        app.TypedPassword = c.Server.Password;

        await using var connection = await app.ConnectSshAsync(host);
        Assert.True(connection.IsConnected);
        Assert.True(app.PasswordPrompts >= 1, "no password prompt reached the user");
    }

    private static async Task PassphraseProtectedKeyAuthenticates(E2EContext c)
    {
        var (app, keyHost) = await c.StartWithKeyHostAsync();
        const string passphrase = "meow-e2e-passphrase";
        string privateKey;
        string publicKey;
        // The server has ssh-keygen and the phone does not: mint the encrypted
        // key there, as a user generating one on their workstation would.
        await using (var setup = await app.ConnectSshAsync(keyHost))
        {
            var dir = $"/tmp/meow-e2e-{Guid.NewGuid():N}";
            await RequireAsync(setup, $"mkdir -p {dir} && ssh-keygen -q -t ed25519 -N '{passphrase}' -C meow-e2e-passphrase -f {dir}/k");
            privateKey = (await RequireAsync(setup, $"cat {dir}/k")).TrimEnd() + "\n";
            publicKey = (await RequireAsync(setup, $"cat {dir}/k.pub")).Trim();
            await RequireAsync(setup, $"rm -rf {dir}");
        }
        Assert.Contains("BEGIN OPENSSH PRIVATE KEY", privateKey, StringComparison.Ordinal);

        await using var authorized = await AuthorizedKey.AddAsync(app, keyHost, publicKey);
        var credential = await app.AddPrivateKeyAsync(c.Server.Username, privateKey, passphrase);
        var host = await app.AddHostAsync(c.Server, credential);

        await using var connection = await app.ConnectSshAsync(host);
        Assert.Equal(c.Server.Username, (await connection.RunCommandAsync("whoami")).StandardOutput.Trim());
    }

    private static async Task NonExportableKeystoreKeyAuthenticates(E2EContext c)
    {
        var (app, keyHost) = await c.StartWithKeyHostAsync();
        var credential = await app.AddHardwareKeyAsync(c.Server.Username);
        Assert.StartsWith("ecdsa-sha2-nistp256 ", credential.PublicKey, StringComparison.Ordinal);

        await using var authorized = await AuthorizedKey.AddAsync(app, keyHost, credential.PublicKey!);
        var host = await app.AddHostAsync(c.Server, credential);

        // The only key this connection has is in the platform key store, so the
        // server accepting it proves the Keystore signed a real SSH challenge.
        await using var connection = await app.ConnectSshAsync(host);
        Assert.Equal(c.Server.Username, (await connection.RunCommandAsync("whoami")).StandardOutput.Trim());
    }

    private static async Task CredentialUsernameOverridesTheHost(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var key = await app.AddPrivateKeyAsync(c.Server.Username, c.Server.ClientPrivateKey);
        var host = await app.AddHostAsync(c.Server, key, h => h with { Username = "no-such-meow-user" });

        await using var connection = await app.ConnectSshAsync(host);
        Assert.Equal(c.Server.Username, (await connection.RunCommandAsync("whoami")).StandardOutput.Trim());
    }

    private static async Task PtyHasTheRequestedSizeAndFollowsResizes(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await using var connection = await app.ConnectAsync(host);
        await using var terminal = new E2ETerminal(await connection.OpenTerminalAsync(100, 30));

        Assert.Equal("30 100", await terminal.CheckAsync("stty size"));
        await terminal.Session.ResizeAsync(132, 43);
        Assert.Equal("43 132", await terminal.CheckAsync("stty size"));
    }

    private static async Task ExecReturnsOutputErrorAndExitCode(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await using var connection = await app.ConnectSshAsync(host);

        var result = await connection.RunCommandAsync("printf out; printf err >&2; exit 3");
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.StandardOutput);
        Assert.Equal("err", result.StandardError);
    }

    private static async Task ShellExitIsReported(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await using var connection = await app.ConnectAsync(host);
        await using var terminal = new E2ETerminal(await connection.OpenTerminalAsync(80, 24));

        await terminal.CheckAsync("true");
        await terminal.SendAsync("exit 7\n");
        var exited = await Task.WhenAny(terminal.ExitCode.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(exited == terminal.ExitCode.Task, "the shell exited but the session never said so");
        Assert.Equal(7, await terminal.ExitCode.Task);
    }

    private static async Task DroppedConnectionIsReportedAndReconnects(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var connection = await app.ConnectAsync(host);
        var lost = new TaskCompletionSource<SshConnectionLost>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ConnectionLost += (_, e) => lost.TrySetResult(e);
        var terminal = new E2ETerminal(await connection.OpenTerminalAsync(80, 24));

        try
        {
            await terminal.CheckAsync("true");
            // Kill the server side of this connection without a clean shutdown:
            // the shell's parent is the sshd process serving it. Same as the
            // server rebooting or the network dropping under an open session.
            await terminal.SendAsync("kill -9 $PPID\n");

            var reported = await Task.WhenAny(lost.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(reported == lost.Task, "the connection died but ConnectionLost never fired");
            Assert.False(connection.IsConnected);
        }
        finally
        {
            await terminal.DisposeAsync();
            await connection.DisposeAsync();
        }

        // What the session screen's Reconnect does: connect the same host again.
        await using var again = await app.ConnectAsync(host);
        await using var second = new E2ETerminal(await again.OpenTerminalAsync(80, 24));
        Assert.Equal("ok", await second.CheckAsync("echo ok"));
    }

    private static async Task<string> RequireAsync(ISshConnection connection, string command)
    {
        var result = await connection.RunCommandAsync(command, TimeSpan.FromSeconds(60));
        if (!result.Succeeded)
            throw new InvalidOperationException($"'{command}' exited {result.ExitCode}: {result.StandardError}{result.StandardOutput}");
        return result.StandardOutput;
    }

    private static string RandomEd25519PublicKey()
    {
        using var blob = new MemoryStream();
        void Write(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
            blob.Write(length);
            blob.Write(value);
        }
        Write("ssh-ed25519"u8);
        Write(RandomNumberGenerator.GetBytes(32));
        return "ssh-ed25519 " + Convert.ToBase64String(blob.ToArray());
    }

    /// <summary>
    /// Adds a public key to the test user's authorized_keys for the life of a
    /// scenario, through an SSH session the way a user would install one.
    /// </summary>
    private sealed class AuthorizedKey(E2EApp app, HostRecord via, string marker) : IAsyncDisposable
    {
        public static async Task<AuthorizedKey> AddAsync(E2EApp app, HostRecord via, string publicKey)
        {
            var marker = "meow-e2e-" + Guid.NewGuid().ToString("N")[..10];
            var line = string.Join(' ', publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)) + " " + marker;
            await using var connection = await app.ConnectSshAsync(via);
            await RequireAsync(connection,
                $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && printf '%s\\n' '{line}' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys");
            return new AuthorizedKey(app, via, marker);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var connection = await app.ConnectSshAsync(via);
                await connection.RunCommandAsync($"sed -i '/ {marker}$/d' ~/.ssh/authorized_keys");
            }
            catch (Exception) { /* the next run's keys are unique; a leftover line is harmless */ }
        }
    }
}
