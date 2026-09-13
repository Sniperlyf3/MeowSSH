using System.Text;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;
using System.Runtime.Versioning;
using Xunit.Abstractions;

namespace MeowSSH.Integration.Tests;

/// <summary>
/// The engine against a real OpenSSH server over a real socket. Nothing here is
/// mocked: a wrong byte in a signature, a wrong terminal mode, or a host key
/// policy that trusts too much all fail here and nowhere else.
/// </summary>
[Collection(nameof(SshServerCollection))]
[SupportedOSPlatform("linux")]
public class SshEngineTests(SshServerFixture server, ITestOutputHelper output)
{
    private HostRecord Host() => new()
    {
        Id = Guid.NewGuid(),
        Label = "test-server",
        Address = "127.0.0.1",
        Port = server.Port,
        Username = SshServerFixture.Username,
        Transport = SshTransport.Tcp,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static MeowshellSshEngine Engine(string workspace, string knownHosts) =>
        new(new MeowshellSshEngineOptions(
            WorkingDirectory: workspace,
            KnownHostsPath: knownHosts,
            ConnectTimeout: TimeSpan.FromSeconds(45)));

    /// <summary>Reads output until <paramref name="marker"/> appears, or times out.</summary>
    private static async Task<string> ReadUntilAsync(ISshShell shell, string marker, TimeSpan timeout)
    {
        var seen = new StringBuilder();
        var found = new TaskCompletionSource();
        void OnOutput(object? _, ReadOnlyMemory<byte> data)
        {
            lock (seen)
            {
                seen.Append(Encoding.UTF8.GetString(data.Span));
                if (seen.ToString().Contains(marker, StringComparison.Ordinal)) found.TrySetResult();
            }
        }

        shell.OutputReceived += OnOutput;
        try
        {
            await Task.WhenAny(found.Task, Task.Delay(timeout));
            lock (seen) return seen.ToString();
        }
        finally { shell.OutputReceived -= OnOutput; }
    }

    [SkippableFact]
    public async Task ConnectsWithAPublicKeyAndRunsACommandInARealShell()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        server.AuthorizeClientKey();

        var workspace = server.WorkspaceFor(nameof(ConnectsWithAPublicKeyAndRunsACommandInARealShell));
        var knownHosts = await server.SeedKnownHostsAsync(workspace);
        using var credentials = SshCredentials.FromPrivateKeyFile(server.ClientKeyPath);
        var prompts = new RecordingPrompts();

        await using var connection = await Engine(workspace, knownHosts).ConnectAsync(Host(), credentials, prompts);
        Assert.True(connection.IsConnected);

        await using var shell = await connection.OpenShellAsync(100, 30);
        await shell.WriteAsync(Encoding.UTF8.GetBytes("echo MEOWSSH_PROOF_$((6*7))\n"));

        var screen = await ReadUntilAsync(shell, "MEOWSSH_PROOF_42", TimeSpan.FromSeconds(25));
        output.WriteLine(screen);

        // The shell evaluated the arithmetic, so this is a real remote shell and
        // not an echo of what was sent.
        Assert.Contains("MEOWSSH_PROOF_42", screen, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheRemotePtyUsesTheSizeItWasGiven()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        server.AuthorizeClientKey();

        var workspace = server.WorkspaceFor(nameof(TheRemotePtyUsesTheSizeItWasGiven));
        var knownHosts = await server.SeedKnownHostsAsync(workspace);
        using var credentials = SshCredentials.FromPrivateKeyFile(server.ClientKeyPath);
        var prompts = new RecordingPrompts();

        await using var connection = await Engine(workspace, knownHosts).ConnectAsync(Host(), credentials, prompts);
        await using var shell = await connection.OpenShellAsync(123, 45);

        await shell.WriteAsync("stty size\n"u8.ToArray());
        var screen = await ReadUntilAsync(shell, "45 123", TimeSpan.FromSeconds(25));
        output.WriteLine(screen);

        // stty reads the kernel's idea of the terminal size, so this proves the
        // pty was actually allocated at the requested dimensions.
        Assert.Contains("45 123", screen, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ResizeReachesTheRemotePty()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        server.AuthorizeClientKey();

        var workspace = server.WorkspaceFor(nameof(ResizeReachesTheRemotePty));
        var knownHosts = await server.SeedKnownHostsAsync(workspace);
        using var credentials = SshCredentials.FromPrivateKeyFile(server.ClientKeyPath);
        var prompts = new RecordingPrompts();

        await using var connection = await Engine(workspace, knownHosts).ConnectAsync(Host(), credentials, prompts);
        await using var shell = await connection.OpenShellAsync(80, 24);

        await shell.ResizeAsync(132, 50);
        await Task.Delay(500);
        await shell.WriteAsync("stty size\n"u8.ToArray());

        var screen = await ReadUntilAsync(shell, "50 132", TimeSpan.FromSeconds(25));
        output.WriteLine(screen);

        // Every rotation and soft-keyboard toggle on a phone does this.
        Assert.Contains("50 132", screen, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AChangedHostKeyIsRefusedAndReportedAsSuch()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        server.AuthorizeClientKey();

        var workspace = server.WorkspaceFor(nameof(AChangedHostKeyIsRefusedAndReportedAsSuch));
        var knownHosts = await server.SeedKnownHostsAsync(workspace);
        using var credentials = SshCredentials.FromPrivateKeyFile(server.ClientKeyPath);
        var prompts = new RecordingPrompts();

        // Trust the server as it is now.
        await using (var first = await Engine(workspace, knownHosts).ConnectAsync(Host(), credentials, prompts))
            Assert.True(first.IsConnected);

        // Now the same address answers with a different identity -- what an
        // interception looks like from the client's side.
        await server.RotateHostKeyAsync();

        var host = Host() with { Port = server.Port };
        var ex = await Assert.ThrowsAsync<SshException>(
            () => Engine(workspace, knownHosts).ConnectAsync(host, credentials, prompts));

        output.WriteLine($"{ex.Failure}: {ex.Message}");

        // Typed, so the UI can show a warning rather than a retry prompt, and
        // never retryable: this needs a decision, not another attempt.
        Assert.Equal(SshFailure.HostKeyChanged, ex.Failure);
        Assert.False(ex.IsRetryable);
        // The prompt handler must not have been consulted: a changed key is not
        // a question to put to the user mid-connection.
        Assert.Empty(prompts.HostKeyPrompts);
    }

    [SkippableFact]
    public async Task AnUnknownHostIsOfferedToTheUserAndRememberedOnceAccepted()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        server.AuthorizeClientKey();

        var workspace = server.WorkspaceFor(nameof(AnUnknownHostIsOfferedToTheUserAndRememberedOnceAccepted));
        var knownHosts = Path.Combine(workspace, "known_hosts");
        using var credentials = SshCredentials.FromPrivateKeyFile(server.ClientKeyPath);
        var prompts = new RecordingPrompts { AcceptHostKeys = true };

        await using (var connection = await Engine(workspace, knownHosts).ConnectAsync(Host(), credentials, prompts))
            Assert.True(connection.IsConnected);

        // Asked, and asked with something the user could act on: a prompt
        // carrying no fingerprint is not a decision, it is a dialog.
        var prompt = Assert.Single(prompts.HostKeyPrompts);
        Assert.NotEmpty(prompt.Fingerprint);
        output.WriteLine($"prompted for {prompt.Host}: {prompt.Fingerprint}");

        // Accepting has to persist, or every connection asks again and the
        // question stops meaning anything.
        Assert.True(File.Exists(knownHosts), "accepting the key did not write a known_hosts entry");

        var second = new RecordingPrompts { AcceptHostKeys = false };
        await using (var connection = await Engine(workspace, knownHosts).ConnectAsync(Host(), credentials, second))
            Assert.True(connection.IsConnected);

        // Not asked again, and it connected even though this run would have
        // refused -- which is what proves the first answer was remembered
        // rather than the question simply not being asked.
        Assert.Empty(second.HostKeyPrompts);
    }

    [SkippableFact]
    public async Task DecliningAnUnknownHostRefusesTheConnection()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        server.AuthorizeClientKey();

        var workspace = server.WorkspaceFor(nameof(DecliningAnUnknownHostRefusesTheConnection));
        var knownHosts = Path.Combine(workspace, "known_hosts");
        using var credentials = SshCredentials.FromPrivateKeyFile(server.ClientKeyPath);
        var prompts = new RecordingPrompts { AcceptHostKeys = false };

        var ex = await Assert.ThrowsAsync<SshException>(
            () => Engine(workspace, knownHosts).ConnectAsync(Host(), credentials, prompts));

        output.WriteLine($"{ex.Failure}: {ex.Message}");

        // The safe outcome, and the assertion that must never weaken: a host the
        // user declined is not connected to, and not recorded as trusted.
        Assert.Equal(SshFailure.HostKeyUnknown, ex.Failure);
        Assert.Single(prompts.HostKeyPrompts);

        // Contents, not existence. The agent creates the file whether or not it
        // writes to it, so asserting it is absent tests the agent's bookkeeping
        // rather than the thing that matters -- which is that nothing was
        // trusted.
        var recorded = File.Exists(knownHosts) ? await File.ReadAllTextAsync(knownHosts) : "";
        output.WriteLine($"known_hosts after declining: {recorded.Length} bytes");
        Assert.DoesNotContain("127.0.0.1", recorded, StringComparison.Ordinal);
        Assert.DoesNotContain("ssh-", recorded, StringComparison.Ordinal);
    }
}

/// <summary>Records what the engine asked, and answers from fixed material.</summary>
internal sealed class RecordingPrompts : ISshPrompts
{
    public string? Password { get; init; }

    public bool AcceptHostKeys { get; init; }

    public List<HostKeyPrompt> HostKeyPrompts { get; } = [];

    public List<string> PasswordPrompts { get; } = [];

    public Task<bool> ConfirmUnknownHostKeyAsync(HostKeyPrompt prompt, CancellationToken cancellationToken = default)
    {
        HostKeyPrompts.Add(prompt);
        return Task.FromResult(AcceptHostKeys);
    }

    public Task<SecretBuffer?> RequestPasswordAsync(string prompt, CancellationToken cancellationToken = default)
    {
        PasswordPrompts.Add(prompt);
        return Task.FromResult(Password is null
            ? null
            : SecretBuffer.CopyFrom(Encoding.UTF8.GetBytes(Password)));
    }

    public Task<SecretBuffer?> RequestKeyPassphraseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<SecretBuffer?>(null);

    public Task<IReadOnlyList<string>?> AnswerChallengeAsync(
        KeyboardInteractivePrompt prompt, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
}
