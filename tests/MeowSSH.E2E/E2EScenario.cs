using System.Diagnostics;
using MeowSSH.Core.Licensing;

namespace MeowSSH.E2E;

/// <summary>One end-to-end behaviour, written once and run on every platform.</summary>
public sealed record E2EScenario(string Name, Func<E2EContext, Task> Run)
{
    public override string ToString() => Name;
}

/// <summary>What a scenario gets: the server, the platform, and app installs it can start.</summary>
public sealed class E2EContext : IAsyncDisposable
{
    private readonly List<E2EApp> _apps = [];
    private readonly List<string> _instances = [];
    private readonly string _root;

    public E2EContext(string scenario, IE2EPlatform platform, E2EServer server, Action<string> log, E2EApi? api = null)
    {
        Api = api;
        Scenario = scenario;
        Platform = platform;
        Server = server;
        Log = log;
        _root = Path.Combine(platform.ScratchRoot, scenario + "-" + Guid.NewGuid().ToString("N")[..6]);
    }

    public string Scenario { get; }
    public IE2EPlatform Platform { get; }
    public E2EServer Server { get; }
    public Action<string> Log { get; }

    /// <summary>The real backend, when the run has one; null otherwise (see <see cref="E2EApi"/>).</summary>
    public E2EApi? Api { get; }

    /// <summary>The backend, for a scenario that cannot run without one.</summary>
    public E2EApi RequireApi() =>
        Api ?? throw new InvalidOperationException("This scenario needs a running MeowSSHAPI (E2E_API_URL).");

    /// <summary>A fresh install of the app with an unlocked vault. Disposed with the context.</summary>
    /// <remarks>
    /// With a backend, the tier is what the API grants for the matching Play
    /// purchase (<see cref="E2EPlayStore.For"/>), or for <paramref name="store"/>.
    /// </remarks>
    public async Task<E2EApp> StartAppAsync(EntitlementTier tier = EntitlementTier.ProCloud, E2EPlayStore? store = null)
    {
        var app = await E2EApp.StartAsync(Platform, Path.Combine(_root, "app" + _instances.Count), tier, api: Api, store: store);
        _instances.Add(app.InstanceId);
        _apps.Add(app);
        return app;
    }

    /// <summary>
    /// Quits <paramref name="app"/> and launches it again on the same data and
    /// key-store entries: what the user sees after the process is killed.
    /// </summary>
    /// <param name="unlock">False leaves an existing vault at the lock screen, for the scenario to unlock.</param>
    public async Task<E2EApp> RestartAsync(E2EApp app, bool unlock = true)
    {
        _apps.Remove(app);
        await app.DisposeAsync();
        var restarted = await E2EApp.StartAsync(Platform, app.DataDirectory, app.Tier, app.InstanceId, unlock,
            app.Api, app.Api is null ? null : app.Store);
        _apps.Add(restarted);
        return restarted;
    }

    /// <summary>A started app with the server saved as a host using its private key.</summary>
    public async Task<(E2EApp App, Core.Model.HostRecord Host)> StartWithKeyHostAsync()
    {
        var app = await StartAppAsync();
        var key = await app.AddPrivateKeyAsync(Server.Username, Server.ClientPrivateKey);
        return (app, await app.AddHostAsync(Server, key));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps) await app.DisposeAsync();
        foreach (var instance in _instances)
        {
            try { await Platform.CleanUpAsync(instance); }
            catch (Exception ex) { Log($"cleanup of {instance} failed: {ex.Message}"); }
        }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed record E2EResult(string Name, bool Passed, TimeSpan Duration, string? Failure);

public static class E2ERunner
{
    public static async Task<E2EResult> RunAsync(E2EScenario scenario, IE2EPlatform platform, E2EServer server, Action<string> log, TimeSpan? timeout = null, E2EApi? api = null)
    {
        var clock = Stopwatch.StartNew();
        await using var context = new E2EContext(scenario.Name, platform, server, log, api);
        try
        {
            var run = scenario.Run(context);
            var limit = timeout ?? TimeSpan.FromMinutes(3);
            if (await Task.WhenAny(run, Task.Delay(limit)) != run)
                throw new TimeoutException($"Scenario did not finish within {limit.TotalMinutes:0} minutes.");
            await run;
            return new E2EResult(scenario.Name, true, clock.Elapsed, null);
        }
        catch (Exception ex)
        {
            return new E2EResult(scenario.Name, false, clock.Elapsed, ex.ToString());
        }
    }
}
