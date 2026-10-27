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
    private readonly string _root;

    public E2EContext(string scenario, IE2EPlatform platform, E2EServer server, Action<string> log)
    {
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

    /// <summary>A fresh install of the app with an unlocked vault. Disposed with the context.</summary>
    public async Task<E2EApp> StartAppAsync(EntitlementTier tier = EntitlementTier.ProCloud)
    {
        var app = await E2EApp.StartAsync(Platform, Path.Combine(_root, "app" + _apps.Count), tier);
        _apps.Add(app);
        return app;
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
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed record E2EResult(string Name, bool Passed, TimeSpan Duration, string? Failure);

public static class E2ERunner
{
    public static async Task<E2EResult> RunAsync(E2EScenario scenario, IE2EPlatform platform, E2EServer server, Action<string> log, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        await using var context = new E2EContext(scenario.Name, platform, server, log);
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
