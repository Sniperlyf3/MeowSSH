using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Services;

public sealed class ParameterizedCommandActionRunnerTests
{
    [Fact]
    public async Task FreeTierFailsBeforeCredentialsOrNetwork()
    {
        var host = Host();
        var store = new MemoryCommandActionStore();
        var action = new CommandAction(Guid.NewGuid(), "Deploy", "deploy {{version}}", [host.Id]);
        await store.SaveAsync(action);
        var credentials = new CountingCredentials();
        var engine = new FakeEngine();
        var runner = new ParameterizedCommandActionRunner(
            store, new FakeDirectory([host]), credentials, engine, new FakePrompts(), new FakeEntitlements(false));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(action.Id, new Dictionary<string, string> { ["version"] = "1.2.3" }));

        Assert.Contains("Pro", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, credentials.ResolveCount);
        Assert.Equal(0, engine.ConnectionCount);
    }

    [Fact]
    public async Task ProRunShellQuotesEphemeralValues()
    {
        var host = Host();
        var store = new MemoryCommandActionStore();
        var action = new CommandAction(Guid.NewGuid(), "Deploy", "deploy --version {{version}} --note {{note}}", [host.Id]);
        await store.SaveAsync(action);
        var engine = new FakeEngine();
        var runner = new ParameterizedCommandActionRunner(
            store, new FakeDirectory([host]), new CountingCredentials(), engine, new FakePrompts(), new FakeEntitlements(true));

        var run = await runner.RunAsync(action.Id, new Dictionary<string, string>
        {
            ["version"] = "1.2.3; touch /tmp/pwned",
            ["note"] = "it's safe",
        });

        Assert.True(run.Succeeded);
        Assert.Equal("deploy --version '1.2.3; touch /tmp/pwned' --note 'it'\"'\"'s safe'", engine.LastCommand);
        Assert.DoesNotContain("1.2.3", action.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateRejectsMissingValuesAndQuotesSingleQuotes()
    {
        Assert.Equal(["name"], CommandActionTemplate.GetVariables("echo {{name}} {{name}}"));
        Assert.Equal("echo 'a'\"'\"'b'", CommandActionTemplate.Render(
            "echo {{name}}",
            new Dictionary<string, string> { ["name"] = "a'b" }));
        Assert.Throws<ArgumentException>(() =>
            CommandActionTemplate.Render("echo {{name}}", new Dictionary<string, string>()));
    }

    private static HostRecord Host() => new()
    {
        Id = Guid.NewGuid(),
        Label = "prod-web",
        Address = "prod-web.example.test",
        Protocol = HostProtocol.Ssh,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FakeDirectory(IReadOnlyList<HostRecord> hosts) : IHostDirectory
    {
        public event EventHandler? Changed { add { } remove { } }
        public ValueTask<IReadOnlyList<HostStatus>> GetHostsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<HostStatus>>([.. hosts.Select(static host => new HostStatus(host))]);
    }

    private sealed class CountingCredentials : ICredentialResolver
    {
        public int ResolveCount { get; private set; }
        public ValueTask<SshCredentials> ResolveAsync(HostRecord host, CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return ValueTask.FromResult(new SshCredentials());
        }
        public ValueTask<SecretBuffer?> ResolvePasswordAsync(HostRecord host, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SecretBuffer?>(null);
    }

    private sealed class FakeEntitlements(bool pro) : IEntitlementService
    {
        public EntitlementSnapshot Current { get; } = pro
            ? new(EntitlementTier.Pro, EntitlementSource.Promotional, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1))
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);
        public event EventHandler? Changed { add { } remove { } }
        public bool Has(PremiumFeature feature) => pro;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }

    private sealed class FakePrompts : ISshPrompts
    {
        public Task<bool> ConfirmUnknownHostKeyAsync(HostKeyPrompt prompt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<SecretBuffer?> RequestPasswordAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult<SecretBuffer?>(null);
        public Task<SecretBuffer?> RequestKeyPassphraseAsync(CancellationToken cancellationToken = default) => Task.FromResult<SecretBuffer?>(null);
        public Task<IReadOnlyList<string>?> AnswerChallengeAsync(KeyboardInteractivePrompt prompt, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>?>([]);
    }

    private sealed class FakeEngine : ISshEngine
    {
        public int ConnectionCount { get; private set; }
        public string? LastCommand { get; private set; }
        public Task<ISshConnection> ConnectAsync(HostRecord host, SshCredentials credentials, ISshPrompts prompts, CancellationToken cancellationToken = default)
        {
            ConnectionCount++;
            return Task.FromResult<ISshConnection>(new FakeConnection(this, host));
        }

        private sealed class FakeConnection(FakeEngine owner, HostRecord host) : ISshConnection
        {
            public Guid HostId => host.Id;
            public bool IsConnected { get; private set; } = true;
            public event EventHandler<SshConnectionLost>? ConnectionLost { add { } remove { } }
            public Task<SshCommandResult> RunCommandAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            {
                owner.LastCommand = command;
                return Task.FromResult(new SshCommandResult(0, "ok", string.Empty));
            }
            public Task<ISshShell> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<ISshForward> OpenLocalForwardAsync(string listenAddress, string remoteAddress, bool allowNonLoopbackBind = false, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<ISshForward> OpenLocalForwardOnUnixSocketAsync(string socketPath, string remoteAddress, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<ISshForward> OpenRemoteForwardAsync(string listenAddress, string localAddress, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<ISshForward> OpenSocksForwardAsync(string listenAddress, bool requireAuth = true, string? socksUsername = null, string? socksPassword = null, bool allowNonLoopbackBind = false, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<ISshForward> OpenSocksForwardOnUnixSocketAsync(string socketPath, bool requireAuth = false, string? socksUsername = null, string? socksPassword = null, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
        }
    }
}
