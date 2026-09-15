using MeowSSH.Core.Licensing;
using MeowSSH.Core.Security;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Services;

public sealed record EncryptedVaultBackupInfo(
    int SchemaVersion,
    string DeviceId,
    long Revision,
    int WrappedKeyCount,
    long SizeBytes);

public interface IEncryptedVaultBackupService
{
    ValueTask<EncryptedVaultBackupInfo> InspectAsync(
        ReadOnlyMemory<byte> backup,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]> ExportAsync(CancellationToken cancellationToken = default);

    ValueTask RestoreAsync(
        ReadOnlyMemory<byte> backup,
        string recoveryCode,
        CancellationToken cancellationToken = default);

    ValueTask RebindDeviceKeyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Portable local backup for the encrypted vault file itself. The export never decrypts hosts,
/// credentials, private keys or passwords: the existing recovery-code-wrapped master key is the
/// cross-device unlock route.
/// </summary>
public sealed class EncryptedVaultBackupService(
    IVaultStorage storage,
    VaultStore vault,
    IDeviceKeyStore deviceKeyStore,
    IEntitlementService entitlements) : IEncryptedVaultBackupService
{
    public const int MaxBackupBytes = 64 * 1024 * 1024;
    public const string FileExtension = ".meowssh-vault";

    public ValueTask<EncryptedVaultBackupInfo> InspectAsync(
        ReadOnlyMemory<byte> backup,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ValidateStructure(backup);
        var header = VaultFile.ReadHeader(bytes.Span);
        return ValueTask.FromResult(new EncryptedVaultBackupInfo(
            header.SchemaVersion,
            header.DeviceId,
            header.Revision,
            header.WrappedKeys.Count,
            bytes.Length));
    }

    public async ValueTask<byte[]> ExportAsync(CancellationToken cancellationToken = default)
    {
        RequirePro();
        var bytes = await storage.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("There is no MeowSSH vault to back up yet.");
        ValidateStructure(bytes);
        return bytes;
    }

    public async ValueTask RestoreAsync(
        ReadOnlyMemory<byte> backup,
        string recoveryCode,
        CancellationToken cancellationToken = default)
    {
        RequirePro();
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryCode);
        var imported = ValidateStructure(backup).ToArray();

        // Prove both the file authentication and recovery code before touching this device's vault.
        // This uses a separate store/key ring and therefore cannot alter the active vault on failure.
        await ValidateRecoveryCodeAsync(imported, recoveryCode, cancellationToken).ConfigureAwait(false);

        var previous = await storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        vault.Lock();
        try
        {
            await storage.WriteAsync(imported, cancellationToken).ConfigureAwait(false);
            await vault.UnlockWithRecoveryCodeAsync(recoveryCode, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            vault.Lock();
            if (previous is null)
                await storage.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            else
                await storage.WriteAsync(previous, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask RebindDeviceKeyAsync(CancellationToken cancellationToken = default)
    {
        RequirePro();
        if (!vault.IsUnlocked)
            throw new InvalidOperationException("Unlock the restored vault before enabling biometric unlock on this device.");
        await vault.RebindDeviceKeyAsync(deviceKeyStore, cancellationToken).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> ValidateStructure(ReadOnlyMemory<byte> backup)
    {
        if (backup.IsEmpty)
            throw new VaultFormatException("The selected backup is empty.");
        if (backup.Length > MaxBackupBytes)
            throw new VaultFormatException($"The selected backup exceeds the {MaxBackupBytes / (1024 * 1024)} MiB safety limit.");

        var header = VaultFile.ReadHeader(backup.Span);
        if (!header.WrappedKeys.Any(static key => key.Method == KeyWrapMethod.RecoveryPassphrase))
            throw new VaultFormatException("This vault has no recovery-code key and cannot be restored on another device.");
        return backup;
    }

    private static async ValueTask ValidateRecoveryCodeAsync(
        byte[] backup,
        string recoveryCode,
        CancellationToken cancellationToken)
    {
        var temporaryStorage = new InMemoryVaultStorage();
        await temporaryStorage.WriteAsync(backup, cancellationToken).ConfigureAwait(false);
        using var temporaryVault = new VaultStore(temporaryStorage);
        await temporaryVault.UnlockWithRecoveryCodeAsync(recoveryCode, cancellationToken).ConfigureAwait(false);
        temporaryVault.Lock();
    }

    private void RequirePro()
    {
        if (!entitlements.Has(PremiumFeature.EncryptedLocalBackup))
            throw new InvalidOperationException("Encrypted local backup and restore require MeowSSH Pro.");
    }
}
