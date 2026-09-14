using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.TestHost.Fakes;

/// <summary>Browser-test credential resolver; fake SSH accepts an empty set.</summary>
public sealed class FakeCredentialResolver : ICredentialResolver
{
    public ValueTask<SshCredentials> ResolveAsync(
        HostRecord host,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SshCredentials());
    }

    public ValueTask<MeowSSH.Core.Security.SecretBuffer?> ResolvePasswordAsync(
        HostRecord host,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<MeowSSH.Core.Security.SecretBuffer?>(null);
    }
}
