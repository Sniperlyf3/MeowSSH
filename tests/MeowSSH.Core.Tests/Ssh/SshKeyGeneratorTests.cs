using System.Security.Cryptography;
using System.Text;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Ssh;

public class SshKeyGeneratorTests
{
    [Fact]
    public void GeneratedRsaKeyCanBeImportedAndHasOpenSshPublicKey()
    {
        var generated = SshKeyGenerator.GenerateRsa("meowssh-test");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(generated.PrivateKey);

        Assert.StartsWith("ssh-rsa ", generated.PublicKey, StringComparison.Ordinal);
        Assert.EndsWith(" meowssh-test", generated.PublicKey, StringComparison.Ordinal);
        Assert.True(rsa.KeySize >= 2048);
    }
}
