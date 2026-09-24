using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Security;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Services;

public sealed record CloudBackupVersion(string Id, DateTimeOffset CreatedAtUtc, long SizeBytes, string Sha256Hex);

/// <param name="Enabled">A credential for the vault currently on this phone is stored.</param>
/// <param name="NeedsRecoveryCode">
/// A credential is stored but for a different vault -- one restored since
/// cloud backup was turned on. Uploads stop until the new vault's recovery
/// code is entered, rather than silently backing it up under the old identity.
/// </param>
public sealed record CloudBackupStatus(bool Enabled, bool NeedsRecoveryCode);

/// <summary><see cref="Exception.Message"/> is written for the user.</summary>
public sealed class CloudBackupException(string? code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string? Code { get; } = code;
}

/// <summary>MeowSSHAPI's /v1/cloud-backups/* surface.</summary>
public interface ICloudBackupApi
{
    Task<CloudBackupVersion> UploadAsync(
        CloudBackupCredential credential,
        SignedEntitlementGrant grant,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    /// <summary>Oldest first; empty when nothing was ever uploaded.</summary>
    Task<IReadOnlyList<CloudBackupVersion>> ListAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default);

    Task<byte[]> DownloadAsync(CloudBackupCredential credential, string versionId, CancellationToken cancellationToken = default);

    Task DeleteAllAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where this phone keeps its cloud backup credential -- platform secure
/// storage in the app. The recovery code itself is never kept: it is shown
/// once at setup and deliberately stored nowhere it could be read back.
/// </summary>
public interface ICloudBackupCredentialStore
{
    Task<CloudBackupCredential?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class MemoryCloudBackupCredentialStore : ICloudBackupCredentialStore
{
    private CloudBackupCredential? _credential;

    public Task<CloudBackupCredential?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_credential);

    public Task SaveAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _credential = null;
        return Task.CompletedTask;
    }
}

public interface ICloudVaultBackupService
{
    /// <summary>Uploading needs Pro Cloud; finding, restoring and deleting do not.</summary>
    bool CanUpload { get; }

    Task<CloudBackupStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Proves <paramref name="recoveryCode"/> unlocks the vault on this phone,
    /// then stores the credential derived from it. A mistyped code would
    /// otherwise upload to a locator the user could never find again.
    /// </summary>
    Task EnableAsync(string recoveryCode, CancellationToken cancellationToken = default);

    /// <summary>Forgets the credential on this phone. Cloud copies are left alone.</summary>
    Task DisableAsync(CancellationToken cancellationToken = default);

    Task<CloudBackupVersion> BackupNowAsync(CancellationToken cancellationToken = default);

    /// <summary>This phone's backups, via the stored credential.</summary>
    Task<IReadOnlyList<CloudBackupVersion>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>A new phone's view: backups findable from the recovery code alone.</summary>
    Task<IReadOnlyList<CloudBackupVersion>> FindAsync(string recoveryCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces this phone's vault with a cloud version, through the same
    /// validate-then-swap-with-rollback path as a local restore, and keeps
    /// the restored vault's credential so backups continue under it.
    /// </summary>
    Task RestoreAsync(string recoveryCode, string versionId, CancellationToken cancellationToken = default);

    /// <summary>Erases every cloud version for this phone's vault, then forgets the credential.</summary>
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}

public sealed class CloudVaultBackupService(
    IVaultStorage storage,
    IEncryptedVaultBackupService backups,
    ICloudBackupApi api,
    ICloudEntitlementGrantSource grants,
    ICloudBackupCredentialStore credentials,
    IEntitlementService entitlements) : ICloudVaultBackupService
{
    /// <summary>Matches MeowSSHAPI's CloudBackupOptions.MaxBackupBytes default.</summary>
    public const int MaxCloudBackupBytes = 8 * 1024 * 1024;

    public bool CanUpload => entitlements.Has(PremiumFeature.CloudBackup);

    public async Task<CloudBackupStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var stored = await credentials.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null) return new CloudBackupStatus(Enabled: false, NeedsRecoveryCode: false);
        var matches = string.Equals(stored.VaultFingerprint, await CurrentFingerprintAsync(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal);
        return new CloudBackupStatus(Enabled: matches, NeedsRecoveryCode: !matches);
    }

    public async Task EnableAsync(string recoveryCode, CancellationToken cancellationToken = default)
    {
        RequireUpload();
        var credential = Derive(recoveryCode);
        var current = await ReadCurrentVaultAsync(cancellationToken).ConfigureAwait(false);
        await ProveRecoveryCodeAsync(current, recoveryCode, cancellationToken).ConfigureAwait(false);
        await credentials.SaveAsync(credential.BoundTo(Fingerprint(current)), cancellationToken).ConfigureAwait(false);
    }

    public Task DisableAsync(CancellationToken cancellationToken = default) => credentials.ClearAsync(cancellationToken);

    public async Task<CloudBackupVersion> BackupNowAsync(CancellationToken cancellationToken = default)
    {
        RequireUpload();
        var credential = await RequireCurrentCredentialAsync(cancellationToken).ConfigureAwait(false);

        // The exact encrypted file a local export produces: nothing here ever
        // decrypts hosts or keys, so the server receives only ciphertext.
        var bytes = await backups.ExportAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxCloudBackupBytes)
            throw new CloudBackupException("backup_too_large", $"This vault is larger than the {MaxCloudBackupBytes / (1024 * 1024)} MiB cloud backup limit. Use an encrypted local backup instead.");

        var grant = await GetGrantAsync(cancellationToken).ConfigureAwait(false);
        return await api.UploadAsync(credential, grant, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudBackupVersion>> ListAsync(CancellationToken cancellationToken = default)
    {
        var credential = await RequireCurrentCredentialAsync(cancellationToken).ConfigureAwait(false);
        return await api.ListAsync(credential, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CloudBackupVersion>> FindAsync(string recoveryCode, CancellationToken cancellationToken = default) =>
        api.ListAsync(Derive(recoveryCode), cancellationToken);

    public async Task RestoreAsync(string recoveryCode, string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        var credential = Derive(recoveryCode);
        var bytes = await api.DownloadAsync(credential, versionId, cancellationToken).ConfigureAwait(false);
        await backups.RestoreFromCloudAsync(bytes, recoveryCode, cancellationToken).ConfigureAwait(false);

        // Bound to the vault that is now on this phone. Stored even without
        // Pro Cloud: it grants nothing by itself, and if the user resubscribes
        // backups pick up where they left off without asking for the code again.
        await credentials.SaveAsync(credential.BoundTo(Fingerprint(bytes)), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        var credential = await credentials.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cloud backup is not turned on for this phone.");
        await api.DeleteAllAsync(credential, cancellationToken).ConfigureAwait(false);
        await credentials.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RequireUpload()
    {
        if (!CanUpload)
            throw new InvalidOperationException("Cloud backup requires MeowSSH Pro Cloud.");
    }

    private async Task<SignedEntitlementGrant> GetGrantAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await grants.GetPaidGrantAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Security.SecurityException or HttpRequestException)
        {
            throw new CloudBackupException("entitlement_unavailable", $"Your Pro Cloud purchase could not be verified: {exception.Message}", exception);
        }
    }

    private async Task<CloudBackupCredential> RequireCurrentCredentialAsync(CancellationToken cancellationToken)
    {
        var stored = await credentials.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Turn on cloud backup first.");
        if (!string.Equals(stored.VaultFingerprint, await CurrentFingerprintAsync(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
            throw new InvalidOperationException("This phone's vault changed since cloud backup was turned on. Enter its recovery code again to keep backing it up.");
        return stored;
    }

    private static CloudBackupCredential Derive(string recoveryCode)
    {
        try
        {
            return CloudBackupCredential.FromRecoveryCode(recoveryCode);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private async Task<byte[]> ReadCurrentVaultAsync(CancellationToken cancellationToken) =>
        await storage.ReadAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("There is no MeowSSH vault on this phone yet.");

    private async Task<string?> CurrentFingerprintAsync(CancellationToken cancellationToken)
    {
        var bytes = await storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Fingerprint(bytes);
    }

    private static string Fingerprint(ReadOnlySpan<byte> vaultFile) =>
        CloudBackupCredential.FingerprintOf(VaultFile.ReadHeader(vaultFile));

    private static async Task ProveRecoveryCodeAsync(byte[] vaultFile, string recoveryCode, CancellationToken cancellationToken)
    {
        // A throwaway store over a copy: proving the code must never be able
        // to lock, rewrite or otherwise disturb the vault that is open now.
        var scratch = new InMemoryVaultStorage();
        await scratch.WriteAsync(vaultFile, cancellationToken).ConfigureAwait(false);
        using var probe = new VaultStore(scratch);
        try
        {
            await probe.UnlockWithRecoveryCodeAsync(recoveryCode, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("That recovery code does not unlock the vault on this phone.", exception);
        }
        finally
        {
            probe.Lock();
        }
    }
}

/// <summary>
/// Talks to MeowSSHAPI with the same base URL the licensing client uses. The
/// ownership secret travels only as a bearer token over HTTPS; the locator in
/// the path is a hash of it and grants nothing on its own.
/// </summary>
public sealed class HttpCloudBackupApi(HttpClient httpClient, LicensingApiOptions options) : ICloudBackupApi
{
    public async Task<CloudBackupVersion> UploadAsync(
        CloudBackupCredential credential,
        SignedEntitlementGrant grant,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Put, credential, "versions");
        request.Headers.Add("X-MeowSSH-Entitlement-Grant", grant.PayloadBase64 + "." + grant.SignatureBase64);
        request.Content = new ReadOnlyMemoryContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(CloudBackupJsonContext.Default.CloudBackupVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new CloudBackupException("empty_response", "The MeowSSH cloud service returned an empty response.");
    }

    public async Task<IReadOnlyList<CloudBackupVersion>> ListAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, credential, "versions");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync(CloudBackupJsonContext.Default.CloudBackupVersionList, cancellationToken).ConfigureAwait(false);
        return body?.Versions ?? [];
    }

    public async Task<byte[]> DownloadAsync(CloudBackupCredential credential, string versionId, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, credential, "versions/" + Uri.EscapeDataString(versionId));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAllAsync(CloudBackupCredential credential, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Delete, credential, null);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(HttpMethod method, CloudBackupCredential credential, string? suffix)
    {
        if (!options.IsConfigured || options.BaseUri is null)
            throw new CloudBackupException("not_configured", "The MeowSSH cloud service is not configured in this build.");
        var path = $"v1/cloud-backups/{credential.Locator}" + (suffix is null ? "" : "/" + suffix);
        var request = new HttpRequestMessage(method, new Uri(options.BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.SecretBase64Url);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new CloudBackupException("network", "Could not reach the MeowSSH cloud service. Check your connection and try again.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. Left as is it
            // escapes the page's handlers and stops the Blazor renderer -- the
            // frozen-screen failure VaultSetupScreen already documents.
            throw new CloudBackupException("timeout", "The MeowSSH cloud service took too long to answer. Try again.", exception);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            string? code = null;
            try
            {
                code = (await response.Content.ReadFromJsonAsync(CloudBackupJsonContext.Default.CloudBackupError, cancellationToken).ConfigureAwait(false))?.Error;
            }
            catch (JsonException) { }
            catch (NotSupportedException) { }
            throw new CloudBackupException(code, Explain(code, response.StatusCode));
        }
    }

    private static string Explain(string? code, HttpStatusCode status) => code switch
    {
        "pro_cloud_required" => "Cloud backup requires MeowSSH Pro Cloud.",
        "expired_grant" or "invalid_grant" or "grant_from_future" or "wrong_package" =>
            "Your Pro Cloud purchase could not be verified right now. Try again in a moment.",
        "backup_too_large" => "This vault is larger than the cloud backup limit. Use an encrypted local backup instead.",
        "not_found" => "That backup is no longer stored. Choose another version.",
        "unauthorized" => "No cloud backup matches that recovery code.",
        "cloud_backup_unavailable" => "Cloud backup is not available on the MeowSSH service yet.",
        _ when status == HttpStatusCode.TooManyRequests => "The MeowSSH cloud service is busy. Try again in a minute.",
        _ => $"The MeowSSH cloud service returned an error ({(int)status}).",
    };
}

internal sealed record CloudBackupVersionList(IReadOnlyList<CloudBackupVersion> Versions);
internal sealed record CloudBackupError(string? Error, string? Message);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CloudBackupVersion))]
[JsonSerializable(typeof(CloudBackupVersionList))]
[JsonSerializable(typeof(CloudBackupError))]
internal sealed partial class CloudBackupJsonContext : JsonSerializerContext
{
}
