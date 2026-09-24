using MeowSSH.Core.Services;

namespace MeowSSH.TestHost.Fakes;

public sealed class FakeEncryptedVaultBackupService : IEncryptedVaultBackupService
{
    private static readonly byte[] DemoBackup = "MEOWSSH-TEST-ENCRYPTED-BACKUP"u8.ToArray();

    public ValueTask<EncryptedVaultBackupInfo> InspectAsync(
        ReadOnlyMemory<byte> backup,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new EncryptedVaultBackupInfo(3, "test-device", 7, 2, backup.Length));
    }

    public ValueTask<byte[]> ExportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DemoBackup.ToArray());
    }

    public ValueTask RestoreAsync(
        ReadOnlyMemory<byte> backup,
        string recoveryCode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(recoveryCode))
            throw new InvalidOperationException("A recovery code is required.");
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreFromCloudAsync(
        ReadOnlyMemory<byte> backup,
        string recoveryCode,
        CancellationToken cancellationToken = default) =>
        RestoreAsync(backup, recoveryCode, cancellationToken);

    public ValueTask RebindDeviceKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
