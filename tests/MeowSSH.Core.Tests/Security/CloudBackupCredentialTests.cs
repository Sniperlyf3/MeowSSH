using MeowSSH.Core.Security;

namespace MeowSSH.Core.Tests.Security;

public sealed class CloudBackupCredentialTests
{
    // Recovery-code entropy 0x00..0x13 through HKDF-SHA256 (salt
    // "meowssh-cloud-backup-v1", info "auth"), computed independently of this
    // code. Pins the whole derivation, not just the final hash.
    private const string VectorRecoveryCode = "000G-40R4-0M30-E209-185G-R38E-1W81-24GK";
    private const string VectorSecretBase64Url = "LNyUm8MMm0ODGf2_CGptGwsXPt6QDBA6ZvYH2agza3Y";
    private const string VectorLocator = "ca7a2a564eb304a43b66144a7b758471f5f6d6362bbd76eef8d522e2d9b897b1";

    [Fact]
    public void ARecoveryCodeDerivesTheKnownAnswerSecretAndLocator()
    {
        var credential = CloudBackupCredential.FromRecoveryCode(VectorRecoveryCode);

        Assert.Equal(VectorSecretBase64Url, credential.SecretBase64Url);
        Assert.Equal(VectorLocator, credential.Locator);
    }

    [Fact]
    public void LocatorMatchesTheServersKnownAnswerVector()
    {
        // The same pair MeowSSHAPI's CloudBackupLocatorTests asserts. If the
        // two sides ever disagree, uploads go to a locator the server rejects.
        byte[] secret = [.. Enumerable.Range(0, 32).Select(static i => (byte)i)];

        Assert.Equal(
            "642cf7a474017f1927ca42f376377835d5fce46a62456fbde2e8130c56649315",
            CloudBackupCredential.LocatorFor(secret));
    }

    [Fact]
    public void TranscriptionRepairsDoNotChangeTheDerivedIdentity()
    {
        // RecoveryCode.Parse forgives case, separators and O/I/L slips; a new
        // phone must find the backup from a code copied off paper that way.
        var sloppy = VectorRecoveryCode.Replace("-", " ").ToLowerInvariant().Replace('0', 'o');

        Assert.Equal(VectorLocator, CloudBackupCredential.FromRecoveryCode(sloppy).Locator);
    }

    [Fact]
    public void DifferentRecoveryCodesNeverShareALocator() =>
        Assert.NotEqual(
            CloudBackupCredential.FromRecoveryCode(RecoveryCode.Generate()).Locator,
            CloudBackupCredential.FromRecoveryCode(RecoveryCode.Generate()).Locator);

    [Fact]
    public void AnIncompleteCodeIsRefusedRatherThanDerivingAnUnfindableIdentity() =>
        Assert.Throws<FormatException>(() => CloudBackupCredential.FromRecoveryCode("000G-40R4-0M30"));
}
