using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowSSH.Core.Licensing;

public sealed record ManagedDerpQuotaRequest(SignedEntitlementGrant Grant);

public sealed record ManagedDerpQuotaState(
    string ClientNodePublic,
    EntitlementTier Tier,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    ulong SentBytes,
    ulong ReceivedBytes,
    ulong UsedBytes,
    ulong AllowanceBytes,
    bool IsExceeded);

public interface IManagedDerpUsageService
{
    Task<ManagedDerpQuotaState> GetCurrentAsync(
        string clientNodePublic,
        CancellationToken cancellationToken = default);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ManagedDerpQuotaRequest))]
[JsonSerializable(typeof(ManagedDerpQuotaState))]
internal sealed partial class ManagedDerpUsageJsonContext : JsonSerializerContext
{
}

public sealed class ManagedDerpUsageService(
    IManagedDerpGrantProvider grants,
    HttpClient httpClient,
    LicensingApiOptions options) : IManagedDerpUsageService
{
    public async Task<ManagedDerpQuotaState> GetCurrentAsync(
        string clientNodePublic,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("The MeowSSH licensing API is not configured.");

        var grant = await grants.GetNodeBoundGrantAsync(clientNodePublic, cancellationToken).ConfigureAwait(false);
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(options.BaseUri!, "v1/derp/usage/current"),
            new ManagedDerpQuotaRequest(grant),
            ManagedDerpUsageJsonContext.Default.ManagedDerpQuotaRequest,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SecurityException("The managed relay service denied the usage request.");
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(
                ManagedDerpUsageJsonContext.Default.ManagedDerpQuotaState,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SecurityException("The managed relay service returned an empty usage response.");
    }
}
