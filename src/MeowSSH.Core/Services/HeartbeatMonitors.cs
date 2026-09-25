using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Services;

/// <summary>Mirrors MeowSSHAPI's HeartbeatState values.</summary>
public static class HeartbeatStates
{
    public const string New = "new";
    public const string Up = "up";
    public const string Late = "late";
    public const string Down = "down";
    public const string Failing = "failing";

    /// <summary>States that page the user. Late is not one: it is a slow run inside its grace window.</summary>
    public static bool IsAlerting(string state) => state is Down or Failing;
}

public sealed record HeartbeatMonitorInfo(
    string Id,
    string Name,
    int PeriodSeconds,
    int GraceSeconds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastPingAtUtc,
    DateTimeOffset? LastFailAtUtc,
    string State,
    DateTimeOffset? DueAtUtc);

public sealed record CreatedHeartbeatMonitor(HeartbeatMonitorInfo Monitor, string PingToken);

/// <summary><see cref="Exception.Message"/> is written for the user.</summary>
public sealed class HeartbeatMonitorException(string? code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string? Code { get; } = code;
}

/// <summary>MeowSSHAPI's /v1/monitors surface.</summary>
public interface IHeartbeatMonitorApi
{
    Task<CreatedHeartbeatMonitor> CreateAsync(string ownerSecret, SignedEntitlementGrant grant, string name, int periodSeconds, int graceSeconds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HeartbeatMonitorInfo>> ListAsync(string ownerSecret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string ownerSecret, string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The phone's monitor identity: a random secret, generated on first use and
/// kept in platform secure storage. Nothing ties it to the Google account,
/// the purchase or the vault.
/// </summary>
public interface IMonitorOwnerSecretStore
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string secret, CancellationToken cancellationToken = default);
}

public sealed class MemoryMonitorOwnerSecretStore : IMonitorOwnerSecretStore
{
    private string? _secret;
    public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_secret);
    public Task SaveAsync(string secret, CancellationToken cancellationToken = default)
    {
        _secret = secret;
        return Task.CompletedTask;
    }
}

/// <summary>
/// What only this phone knows about its monitors: each ping token (the server
/// keeps just a hash, so this is the one place the URL can be shown again) and
/// the state last alerted on, so a monitor that stays down notifies once.
/// </summary>
public sealed record HeartbeatLocalState(Dictionary<string, string> PingTokens, Dictionary<string, string> LastAlertedStates)
{
    public static HeartbeatLocalState Empty() => new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
}

public interface IHeartbeatLocalStore
{
    Task<HeartbeatLocalState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(HeartbeatLocalState state, CancellationToken cancellationToken = default);
}

public sealed class MemoryHeartbeatLocalStore : IHeartbeatLocalStore
{
    private HeartbeatLocalState _state = HeartbeatLocalState.Empty();
    public Task<HeartbeatLocalState> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new HeartbeatLocalState(new(_state.PingTokens, StringComparer.Ordinal), new(_state.LastAlertedStates, StringComparer.Ordinal)));
    public Task SaveAsync(HeartbeatLocalState state, CancellationToken cancellationToken = default)
    {
        _state = state;
        return Task.CompletedTask;
    }
}

/// <summary>App-private file. A ping token lets whoever holds it report a job healthy, not read or change anything.</summary>
public sealed class FileHeartbeatLocalStore(string path) : IHeartbeatLocalStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<HeartbeatLocalState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return HeartbeatLocalState.Empty();
            var document = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                HeartbeatJsonContext.Default.LocalDocument);
            return new HeartbeatLocalState(
                new(document?.PingTokens ?? [], StringComparer.Ordinal),
                new(document?.LastAlertedStates ?? [], StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return HeartbeatLocalState.Empty();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(HeartbeatLocalState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(
                new LocalDocument(state.PingTokens, state.LastAlertedStates), HeartbeatJsonContext.Default.LocalDocument), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    internal sealed record LocalDocument(Dictionary<string, string> PingTokens, Dictionary<string, string> LastAlertedStates);
}

public sealed record HeartbeatAlert(string MonitorId, string Title, string Message);

public interface IHeartbeatAlertSink
{
    Task NotifyAsync(HeartbeatAlert alert, CancellationToken cancellationToken = default);
}

public sealed class NoOpHeartbeatAlertSink : IHeartbeatAlertSink
{
    public Task NotifyAsync(HeartbeatAlert alert, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Keeps the background check scheduled exactly while there is something to check.</summary>
public interface IHeartbeatCheckScheduler
{
    void Update(bool anyMonitors);
}

public sealed class NoOpHeartbeatCheckScheduler : IHeartbeatCheckScheduler
{
    public void Update(bool anyMonitors) { }
}

/// <summary>Android 13+ asks before an app may post notifications; older versions and tests always allow.</summary>
public interface INotificationConsent
{
    /// <returns>Whether notifications can be shown after asking (if asking was needed).</returns>
    Task<bool> EnsureAsync();
}

public sealed class AlwaysGrantedNotificationConsent : INotificationConsent
{
    public Task<bool> EnsureAsync() => Task.FromResult(true);
}

public interface IHeartbeatMonitorService
{
    /// <summary>Creating monitors and background alerts are Pro Cloud; listing and deleting are not.</summary>
    bool CanUse { get; }

    Task<IReadOnlyList<HeartbeatMonitorInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<HeartbeatMonitorInfo> CreateAsync(string name, TimeSpan period, TimeSpan grace, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Null when this phone does not hold the monitor's token (it was created elsewhere, or before a reinstall).</summary>
    Task<Uri?> PingUrlAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>What the background check does: alert once per change into or out of trouble.</summary>
    Task<IReadOnlyList<HeartbeatAlert>> CheckForAlertsAsync(CancellationToken cancellationToken = default);
}

public sealed class HeartbeatMonitorService(
    IHeartbeatMonitorApi api,
    ICloudEntitlementGrantSource grants,
    IEntitlementService entitlements,
    IMonitorOwnerSecretStore owners,
    IHeartbeatLocalStore local,
    IHeartbeatAlertSink alerts,
    IHeartbeatCheckScheduler scheduler,
    LicensingApiOptions options) : IHeartbeatMonitorService
{
    public bool CanUse => entitlements.Has(PremiumFeature.PushMonitoring);

    public async Task<IReadOnlyList<HeartbeatMonitorInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        // No secret yet means no monitors yet: asking the server would only
        // mint an identity for a phone that never used the feature.
        var secret = await owners.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (secret is null) return [];
        var monitors = await api.ListAsync(secret, cancellationToken).ConfigureAwait(false);
        scheduler.Update(monitors.Count > 0);
        return monitors;
    }

    public async Task<HeartbeatMonitorInfo> CreateAsync(string name, TimeSpan period, TimeSpan grace, CancellationToken cancellationToken = default)
    {
        if (!CanUse) throw new InvalidOperationException("Push monitoring requires MeowSSH Pro Cloud.");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the monitor a name, such as the job it watches.");

        var secret = await owners.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await owners.SaveAsync(secret, cancellationToken).ConfigureAwait(false);
        }

        SignedEntitlementGrant grant;
        try
        {
            grant = await grants.GetPaidGrantAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Security.SecurityException or HttpRequestException)
        {
            throw new HeartbeatMonitorException("entitlement_unavailable", $"Your Pro Cloud purchase could not be verified: {exception.Message}", exception);
        }

        var created = await api.CreateAsync(secret, grant, name.Trim(), (int)period.TotalSeconds, (int)grace.TotalSeconds, cancellationToken).ConfigureAwait(false);
        var state = await local.LoadAsync(cancellationToken).ConfigureAwait(false);
        state.PingTokens[created.Monitor.Id] = created.PingToken;
        await local.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        scheduler.Update(anyMonitors: true);
        return created.Monitor;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var secret = await owners.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This phone has no monitors.");
        await api.DeleteAsync(secret, id, cancellationToken).ConfigureAwait(false);
        var state = await local.LoadAsync(cancellationToken).ConfigureAwait(false);
        state.PingTokens.Remove(id);
        state.LastAlertedStates.Remove(id);
        await local.SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Uri?> PingUrlAsync(string id, CancellationToken cancellationToken = default)
    {
        if (options.BaseUri is null) return null;
        var state = await local.LoadAsync(cancellationToken).ConfigureAwait(false);
        return state.PingTokens.TryGetValue(id, out var token) ? new Uri(options.BaseUri, "v1/ping/" + token) : null;
    }

    public async Task<IReadOnlyList<HeartbeatAlert>> CheckForAlertsAsync(CancellationToken cancellationToken = default)
    {
        // A lapsed subscription stops background alerts, not the monitors:
        // they stay listed and deletable, and resume paging on renewal.
        if (!CanUse) return [];
        var secret = await owners.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (secret is null) return [];

        var monitors = await api.ListAsync(secret, cancellationToken).ConfigureAwait(false);
        var state = await local.LoadAsync(cancellationToken).ConfigureAwait(false);
        var raised = new List<HeartbeatAlert>();
        foreach (var monitor in monitors)
        {
            state.LastAlertedStates.TryGetValue(monitor.Id, out var previous);
            var wasAlerting = previous is not null && HeartbeatStates.IsAlerting(previous);
            var isAlerting = HeartbeatStates.IsAlerting(monitor.State);

            HeartbeatAlert? alert = null;
            if (isAlerting && previous != monitor.State)
                alert = new HeartbeatAlert(monitor.Id, TitleFor(monitor), MessageFor(monitor));
            else if (wasAlerting && monitor.State == HeartbeatStates.Up)
                alert = new HeartbeatAlert(monitor.Id, $"{monitor.Name} recovered", "It checked in again.");

            if (alert is not null)
            {
                await alerts.NotifyAsync(alert, cancellationToken).ConfigureAwait(false);
                raised.Add(alert);
            }
            // Late and New carry no alert and leave the last alerted state
            // alone: down -> late -> down is one outage, not two.
            if (isAlerting || monitor.State == HeartbeatStates.Up) state.LastAlertedStates[monitor.Id] = monitor.State;
        }

        foreach (var gone in state.LastAlertedStates.Keys.Except(monitors.Select(m => m.Id)).ToList())
            state.LastAlertedStates.Remove(gone);
        await local.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        scheduler.Update(monitors.Count > 0);
        return raised;
    }

    private static string TitleFor(HeartbeatMonitorInfo monitor) =>
        monitor.State == HeartbeatStates.Failing ? $"{monitor.Name} reported a failure" : $"{monitor.Name} stopped checking in";

    private static string MessageFor(HeartbeatMonitorInfo monitor) =>
        monitor.State == HeartbeatStates.Failing
            ? "Its job called the /fail URL."
            : monitor.LastPingAtUtc is { } last
                ? $"No ping since {last.ToLocalTime():yyyy-MM-dd HH:mm}."
                : "It has not pinged yet.";
}

public sealed class HttpHeartbeatMonitorApi(HttpClient httpClient, LicensingApiOptions options) : IHeartbeatMonitorApi
{
    public async Task<CreatedHeartbeatMonitor> CreateAsync(string ownerSecret, SignedEntitlementGrant grant, string name, int periodSeconds, int graceSeconds, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, "v1/monitors", ownerSecret);
        request.Headers.Add("X-MeowSSH-Entitlement-Grant", grant.PayloadBase64 + "." + grant.SignatureBase64);
        request.Content = JsonContent.Create(new CreateBody(name, periodSeconds, graceSeconds), HeartbeatJsonContext.Default.CreateBody);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(HeartbeatJsonContext.Default.CreatedHeartbeatMonitor, cancellationToken).ConfigureAwait(false)
            ?? throw new HeartbeatMonitorException("empty_response", "The MeowSSH monitoring service returned an empty response.");
    }

    public async Task<IReadOnlyList<HeartbeatMonitorInfo>> ListAsync(string ownerSecret, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "v1/monitors", ownerSecret);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(HeartbeatJsonContext.Default.ListBody, cancellationToken).ConfigureAwait(false))?.Monitors ?? [];
    }

    public async Task DeleteAsync(string ownerSecret, string id, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Delete, "v1/monitors/" + Uri.EscapeDataString(id), ownerSecret);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string ownerSecret)
    {
        if (!options.IsConfigured || options.BaseUri is null)
            throw new HeartbeatMonitorException("not_configured", "The MeowSSH monitoring service is not configured in this build.");
        var request = new HttpRequestMessage(method, new Uri(options.BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerSecret);
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
            throw new HeartbeatMonitorException("network", "Could not reach the MeowSSH monitoring service. Check your connection and try again.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HeartbeatMonitorException("timeout", "The MeowSSH monitoring service took too long to answer. Try again.", exception);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            string? code = null;
            try
            {
                code = (await response.Content.ReadFromJsonAsync(HeartbeatJsonContext.Default.ErrorBody, cancellationToken).ConfigureAwait(false))?.Error;
            }
            catch (JsonException) { }
            catch (NotSupportedException) { }
            throw new HeartbeatMonitorException(code, Explain(code, response.StatusCode));
        }
    }

    private static string Explain(string? code, HttpStatusCode status) => code switch
    {
        "pro_cloud_required" => "Push monitoring requires MeowSSH Pro Cloud.",
        "expired_grant" or "invalid_grant" or "grant_from_future" or "wrong_package" =>
            "Your Pro Cloud purchase could not be verified right now. Try again in a moment.",
        "monitor_limit" => "You have reached the monitor limit. Delete one first.",
        "invalid_request" => "That monitor's settings were not accepted. Check the name and times.",
        _ when status == HttpStatusCode.TooManyRequests => "The MeowSSH monitoring service is busy. Try again in a minute.",
        _ => $"The MeowSSH monitoring service returned an error ({(int)status}).",
    };

    internal sealed record CreateBody(string Name, int PeriodSeconds, int GraceSeconds);
    internal sealed record ListBody(IReadOnlyList<HeartbeatMonitorInfo> Monitors);
    internal sealed record ErrorBody(string? Error, string? Message);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HttpHeartbeatMonitorApi.CreateBody))]
[JsonSerializable(typeof(HttpHeartbeatMonitorApi.ListBody))]
[JsonSerializable(typeof(HttpHeartbeatMonitorApi.ErrorBody))]
[JsonSerializable(typeof(CreatedHeartbeatMonitor))]
[JsonSerializable(typeof(FileHeartbeatLocalStore.LocalDocument))]
internal sealed partial class HeartbeatJsonContext : JsonSerializerContext
{
}
