using System.Text;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Tests.Storage;

public sealed class VaultMigrationTests
{
    [Fact]
    public void Schema3VaultOpensAndRewritesAsCurrentSchemaWithoutLosingExistingFields()
    {
        using var keyRing = VaultKeyRing.CreateNew();
        var credentialId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var hostId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var oldDocument = VaultDocument.CreateEmpty("beta-device") with
        {
            Revision = 42,
            Hosts =
            [
                new HostRecord
                {
                    Id = hostId,
                    Label = "beta-prod",
                    Address = "10.20.30.40",
                    Port = 2202,
                    Username = "deploy",
                    Transport = SshTransport.TailscaleSsh,
                    Protocol = HostProtocol.Ssh,
                    AutoReconnect = false,
                    ProxyUrl = "socks5://127.0.0.1:1080",
                    ForwardAgent = true,
                    Tags = ["prod", "beta"],
                    CredentialId = credentialId,
                    LastConnectedAt = DateTimeOffset.UnixEpoch.AddDays(20),
                    Revision = 9,
                    UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(21),
                    OriginDeviceId = "beta-device",
                    // These did not exist in schema 3 and must not leak into the
                    // historical byte stream produced below.
                    Group = "must-not-exist-in-v3",
                    IsFavorite = true,
                },
            ],
            Credentials =
            [
                new CredentialRecord
                {
                    Id = credentialId,
                    Label = "beta-key",
                    Kind = CredentialKind.PrivateKey,
                    Username = "deploy",
                    Secret = Encoding.UTF8.GetBytes("private-key-material"),
                    Passphrase = Encoding.UTF8.GetBytes("passphrase"),
                    PublicKey = "ssh-ed25519 AAAATEST beta",
                    Revision = 4,
                    UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(19),
                    OriginDeviceId = "beta-device",
                },
            ],
        };

        var schema3Bytes = VaultFile.Write(oldDocument, keyRing, schemaVersion: 3);
        Assert.Equal(3, VaultFile.ReadHeader(schema3Bytes).SchemaVersion);

        var migrated = VaultFile.Read(schema3Bytes, keyRing);
        var host = Assert.Single(migrated.Hosts);
        var credential = Assert.Single(migrated.Credentials);

        Assert.Equal(hostId, host.Id);
        Assert.Equal("beta-prod", host.Label);
        Assert.Equal("10.20.30.40", host.Address);
        Assert.Equal(2202, host.Port);
        Assert.Equal("deploy", host.Username);
        Assert.Equal(SshTransport.TailscaleSsh, host.Transport);
        Assert.False(host.AutoReconnect);
        Assert.Equal("socks5://127.0.0.1:1080", host.ProxyUrl);
        Assert.True(host.ForwardAgent);
        Assert.Equal(["prod", "beta"], host.Tags);
        Assert.Equal(credentialId, host.CredentialId);
        Assert.Null(host.Group);
        Assert.False(host.IsFavorite);

        Assert.Equal(credentialId, credential.Id);
        Assert.Equal("beta-key", credential.Label);
        Assert.Equal(CredentialKind.PrivateKey, credential.Kind);
        Assert.Equal("deploy", credential.Username);
        Assert.Equal("private-key-material", Encoding.UTF8.GetString(credential.Secret));
        Assert.Equal("passphrase", Encoding.UTF8.GetString(credential.Passphrase!));
        Assert.Equal("ssh-ed25519 AAAATEST beta", credential.PublicKey);

        var currentBytes = VaultFile.Write(migrated, keyRing);
        Assert.Equal(VaultDocument.SchemaVersion, VaultFile.ReadHeader(currentBytes).SchemaVersion);
        var reopened = VaultFile.Read(currentBytes, keyRing);
        Assert.Equal(host.Id, Assert.Single(reopened.Hosts).Id);
        Assert.Equal(credential.Id, Assert.Single(reopened.Credentials).Id);
    }
}
