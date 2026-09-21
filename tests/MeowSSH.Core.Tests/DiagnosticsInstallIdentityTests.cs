using MeowSSH.Core.Diagnostics;

namespace MeowSSH.Core.Tests;

public sealed class DiagnosticsInstallIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "meowssh-diagnostics-install-id-tests",
        Guid.NewGuid().ToString("N"));

    private string IdPath => Path.Combine(_root, "install-id");
    private string PreferencePath => Path.Combine(_root, "send-enabled");

    private DiagnosticsInstallIdentity NewIdentity() => new(
        new FileDiagnosticsInstallIdStore(IdPath),
        new FileDiagnosticsSendPreferenceStore(PreferencePath));

    [Fact]
    public async Task IdIsStableAcrossRestartsOnceDiagnosticsAreOn()
    {
        var identity = NewIdentity();
        await identity.SetEnabledAsync(true);
        var first = await identity.GetIdForReportAsync();

        // A fresh instance over the same files stands in for the app restarting:
        // nothing but the on-disk file should carry the id across that boundary.
        var reopened = NewIdentity();
        var second = await reopened.GetIdForReportAsync();

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task TwoFreshInstallsGetDifferentIds()
    {
        var installA = new DiagnosticsInstallIdentity(
            new FileDiagnosticsInstallIdStore(Path.Combine(_root, "a", "install-id")),
            new FileDiagnosticsSendPreferenceStore(Path.Combine(_root, "a", "send-enabled")));
        var installB = new DiagnosticsInstallIdentity(
            new FileDiagnosticsInstallIdStore(Path.Combine(_root, "b", "install-id")),
            new FileDiagnosticsSendPreferenceStore(Path.Combine(_root, "b", "send-enabled")));

        await installA.SetEnabledAsync(true);
        await installB.SetEnabledAsync(true);

        var idA = await installA.GetIdForReportAsync();
        var idB = await installB.GetIdForReportAsync();

        Assert.NotNull(idA);
        Assert.NotNull(idB);
        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public async Task IdIsNotDerivedFromAnyDeviceOrAccountProperty()
    {
        // Regression test for "someone helpfully makes this a device fingerprint":
        // feed the store a value that looks like it was derived from a stable
        // device/account property (a stand-in for IMEI/Android ID/MAC/advertising
        // id/email), and confirm the store never accepts or reproduces it -- it
        // always mints its own random value instead, because GetOrCreateAsync only
        // trusts text shaped like a GUID it wrote itself.
        var deviceProperty = "device-account-fingerprint-not-random";
        var directory = Path.GetDirectoryName(IdPath)!;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(IdPath, deviceProperty);

        var identity = NewIdentity();
        await identity.SetEnabledAsync(true);
        var id = await identity.GetIdForReportAsync();

        Assert.NotNull(id);
        Assert.NotEqual(deviceProperty, id);
        Assert.True(Guid.TryParseExact(id, "D", out _), "The minted id must be a GUID, not a passed-through device value.");

        // Two identities seeded with the exact same fake "device property" as their
        // starting state must still diverge once each mints its own id -- if the id
        // were derived from that shared input, they would come out identical.
        var otherRoot = Path.Combine(_root, "other-device-same-property");
        var otherIdPath = Path.Combine(otherRoot, "install-id");
        Directory.CreateDirectory(otherRoot);
        await File.WriteAllTextAsync(otherIdPath, deviceProperty);
        var otherIdentity = new DiagnosticsInstallIdentity(
            new FileDiagnosticsInstallIdStore(otherIdPath),
            new FileDiagnosticsSendPreferenceStore(Path.Combine(otherRoot, "send-enabled")));
        await otherIdentity.SetEnabledAsync(true);
        var otherId = await otherIdentity.GetIdForReportAsync();

        Assert.NotEqual(id, otherId);
    }

    [Fact]
    public async Task ClearingProducesADifferentId()
    {
        var identity = NewIdentity();
        await identity.SetEnabledAsync(true);
        var original = await identity.GetIdForReportAsync();

        await identity.ResetIdAsync();
        var afterReset = await identity.GetIdForReportAsync();

        Assert.NotNull(original);
        Assert.NotNull(afterReset);
        Assert.NotEqual(original, afterReset);
    }

    [Fact]
    public async Task TurningDiagnosticsOffDeletesTheStoredId()
    {
        var identity = NewIdentity();
        await identity.SetEnabledAsync(true);
        await identity.GetIdForReportAsync();
        Assert.True(File.Exists(IdPath));

        await identity.SetEnabledAsync(false);

        // Off has to delete the file, not just stop reading it: an id left on disk
        // is one re-enable away from resuming as the same install, which is exactly
        // what turning diagnostics off is supposed to prevent.
        Assert.False(File.Exists(IdPath));
    }

    [Fact]
    public async Task WithDiagnosticsDisabledNoIdIsReturnedOrGenerated()
    {
        var identity = NewIdentity();

        var id = await identity.GetIdForReportAsync();

        Assert.Null(id);
        Assert.False(File.Exists(IdPath), "Disabled diagnostics must not mint an id file at all.");
    }

    [Fact]
    public async Task DiagnosticsAreOffByDefaultForANewInstall()
    {
        var identity = NewIdentity();

        Assert.False(await identity.IsEnabledAsync());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
