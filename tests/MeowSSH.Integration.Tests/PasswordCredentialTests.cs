using System.Runtime.Versioning;
using System.Text;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Integration.Tests;

[Collection(nameof(SshServerCollection))]
[SupportedOSPlatform("linux")]
public sealed class PasswordCredentialTests(SshServerFixture server)
{
    [SkippableFact]
    public async Task StoredCredentialUsernameAndPasswordAuthenticateWithoutPrompting()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");

        var workspace = server.WorkspaceFor(nameof(StoredCredentialUsernameAndPasswordAuthenticateWithoutPrompting));
        var knownHosts = await server.SeedKnownHostsAsync(workspace);
        var host = new HostRecord
        {
            Id = Guid.NewGuid(),
            Label = "password-test",
            Address = "127.0.0.1",
            Port = server.Port,
            // Deliberately wrong. Selecting a credential with a username must
            // override this host-level default for authentication.
            Username = "wrong-user",
            Transport = SshTransport.Tcp,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        using var credentials = new SshCredentials
        {
            Username = SshServerFixture.Username,
            Password = SecretBuffer.CopyFrom(Encoding.UTF8.GetBytes(SshServerFixture.Password)),
        };
        var prompts = new RecordingPrompts();
        var engine = new MeowshellSshEngine(new MeowshellSshEngineOptions(
            WorkingDirectory: workspace,
            KnownHostsPath: knownHosts,
            ConnectTimeout: TimeSpan.FromSeconds(45)));

        await using var connection = await engine.ConnectAsync(host, credentials, prompts);

        Assert.True(connection.IsConnected);
        Assert.Empty(prompts.PasswordPrompts);
    }
}
