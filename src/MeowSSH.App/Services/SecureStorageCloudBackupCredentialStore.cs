using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;

namespace MeowSSH.App.Services;

/// <summary>
/// Android Keystore-backed storage for the derived cloud backup credential,
/// alongside the entitlement cache. Holds the derived secret, never the
/// recovery code it came from.
/// </summary>
public sealed class SecureStorageCloudBackupCredentialStore : ICloudBackupCredentialStore
{
    private const string Key = "meowssh.cloud-backup.credential.v1";

    public async Task<CloudBackupCredential?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await SecureStorage.Default.GetAsync(Key).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize(json, CloudBackupCredentialJsonContext.Default.CloudBackupCredential);
        }
        catch (JsonException)
        {
            SecureStorage.Default.Remove(Key);
            return null;
        }
    }

    public Task SaveAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();
        return SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(credential, CloudBackupCredentialJsonContext.Default.CloudBackupCredential));
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(Key);
        return Task.CompletedTask;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CloudBackupCredential))]
internal sealed partial class CloudBackupCredentialJsonContext : JsonSerializerContext
{
}
