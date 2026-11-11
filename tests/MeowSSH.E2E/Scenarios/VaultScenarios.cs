using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using Xunit;

namespace MeowSSH.E2E.Scenarios;

/// <summary>
/// The vault across the app's life: relaunching, locking, losing the device
/// key and coming back with the recovery code, and moving everything to a new
/// install with an encrypted backup. On the emulator the device key is a real
/// Keystore key, so "lost" is a real deleted Keystore entry.
/// </summary>
public static class VaultScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        new("vault.relaunch-unlocks-with-device-key", RelaunchUnlocksAndKeepsEverything),
        new("vault.lock-blocks-secrets", LockedVaultReleasesNoSecrets),
        new("vault.recovery-code-after-device-key-lost", RecoveryCodeReopensAndRebinds),
        new("backup.export-restore-to-new-install", BackupMovesHostsAndKeysToANewInstall),
    ];

    private static async Task RelaunchUnlocksAndKeepsEverything(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        app = await c.RestartAsync(app, unlock: false);
        var vault = app.Get<IVaultSession>();
        Assert.Equal(VaultState.Locked, vault.State);

        Assert.True((await vault.UnlockAsync()).Succeeded);
        // The private key came back out of the vault intact: it still logs in.
        await using var ssh = await app.ConnectSshAsync(host);
        Assert.Equal(c.Server.Username, (await ssh.RunCommandAsync("whoami")).StandardOutput.Trim());
    }

    private static async Task LockedVaultReleasesNoSecrets(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var vault = app.Get<IVaultSession>();

        await vault.LockAsync();
        Assert.Equal(VaultState.Locked, vault.State);
        await Assert.ThrowsAnyAsync<Exception>(() => app.ConnectAsync(host));

        Assert.True((await vault.UnlockAsync()).Succeeded);
        await using var ssh = await app.ConnectSshAsync(host);
        Assert.True(ssh.IsConnected);
    }

    private static async Task RecoveryCodeReopensAndRebinds(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var recoveryCode = app.RecoveryCode!;

        // The device key is gone (a new fingerprint enrolled, a factory key
        // reset): only the recovery code can open the vault now.
        await app.Get<IDeviceKeyStore>().DeleteWrappingKeyAsync();
        app = await c.RestartAsync(app, unlock: false);
        var vault = app.Get<IVaultSession>();
        var refused = await vault.UnlockAsync();
        Assert.False(refused.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(refused.Message));

        Assert.False((await vault.UnlockWithRecoveryCodeAsync(WrongCode(recoveryCode))).Succeeded);
        var recovered = await vault.UnlockWithRecoveryCodeAsync(recoveryCode);
        Assert.True(recovered.Succeeded, recovered.Message);
        await using (var ssh = await app.ConnectSshAsync(host))
            Assert.True(ssh.IsConnected);

        // Recovery gave biometric unlock back: the next launch needs no code.
        app = await c.RestartAsync(app, unlock: false);
        Assert.True((await app.Get<IVaultSession>().UnlockAsync()).Succeeded);
    }

    private static async Task BackupMovesHostsAndKeysToANewInstall(E2EContext c)
    {
        var (oldPhone, host) = await c.StartWithKeyHostAsync();
        var recoveryCode = oldPhone.RecoveryCode!;
        var backups = oldPhone.Get<IEncryptedVaultBackupService>();
        var backup = await backups.ExportAsync();
        // What the restore screen shows before asking for the code.
        var info = await backups.InspectAsync(backup);
        Assert.Equal(backup.Length, info.SizeBytes);
        Assert.False(string.IsNullOrWhiteSpace(info.DeviceId));
        Assert.True(info.Revision > 0, "a vault holding a host and a key has revision 0");
        // Encrypted: neither the key nor the host name appears in the file.
        var text = System.Text.Encoding.UTF8.GetString(backup);
        Assert.DoesNotContain("OPENSSH PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Label, text, StringComparison.Ordinal);

        var newPhone = await c.StartAppAsync();
        var restore = newPhone.Get<IEncryptedVaultBackupService>();
        await Assert.ThrowsAnyAsync<Exception>(() => restore.RestoreAsync(backup, WrongCode(recoveryCode)).AsTask());
        await restore.RestoreAsync(backup, recoveryCode);
        await restore.RebindDeviceKeyAsync();

        var restored = Assert.Single(await newPhone.Get<IHostDirectory>().GetHostsAsync(), h => h.Host.Id == host.Id).Host;
        await using (var ssh = await newPhone.ConnectSshAsync(restored))
            Assert.Equal(c.Server.Username, (await ssh.RunCommandAsync("whoami")).StandardOutput.Trim());

        // And the restored vault opens with the new device's own key.
        newPhone = await c.RestartAsync(newPhone, unlock: false);
        Assert.True((await newPhone.Get<IVaultSession>().UnlockAsync()).Succeeded);
        Assert.Contains(await newPhone.Get<IHostDirectory>().GetHostsAsync(), h => h.Host.Id == host.Id);
    }

    /// <summary>The same code with one character changed to another valid one.</summary>
    private static string WrongCode(string code)
    {
        var chars = code.ToCharArray();
        var i = Array.FindIndex(chars, char.IsLetterOrDigit);
        var alphabet = code.Where(char.IsLetterOrDigit).Distinct().ToArray();
        chars[i] = alphabet.First(ch => ch != chars[i]);
        return new string(chars);
    }
}
