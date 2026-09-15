using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;

namespace MeowSSH.App.Services;

public sealed class SecureStorageEntitlementCache : IEntitlementCache
{
    private const string CacheKey = "meowssh.entitlement.verified.v1";

    public async Task<EntitlementSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await SecureStorage.Default.GetAsync(CacheKey).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize(json, EntitlementJsonContext.Default.EntitlementSnapshot);
        }
        catch (JsonException)
        {
            SecureStorage.Default.Remove(CacheKey);
            return null;
        }
    }

    public Task SaveAsync(EntitlementSnapshot entitlement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(entitlement, EntitlementJsonContext.Default.EntitlementSnapshot);
        return SecureStorage.Default.SetAsync(CacheKey, json);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(CacheKey);
        return Task.CompletedTask;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EntitlementSnapshot))]
internal sealed partial class EntitlementJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Fail-closed provider used until the Play/backend verifier is configured.
/// It intentionally cannot create a paid entitlement locally.
/// </summary>
public sealed class UnconfiguredEntitlementGrantProvider : IEntitlementGrantProvider
{
    private readonly TimeProvider _timeProvider;

    public UnconfiguredEntitlementGrantProvider(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EntitlementSnapshot.Free(_timeProvider.GetUtcNow()));
    }

    public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);
}
