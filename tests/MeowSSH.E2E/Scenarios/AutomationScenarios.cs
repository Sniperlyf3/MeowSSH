using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using Xunit;

namespace MeowSSH.E2E.Scenarios;

/// <summary>
/// The features that run commands for the user: Actions (single, multi-host,
/// parameterised, multi-step), the host health dashboard, command monitoring,
/// session logs and broadcast input -- plus the Free tier being refused the
/// Pro ones. Every side effect is checked on the server.
/// </summary>
public static class AutomationScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        new("actions.multi-host", MultiHostActionReportsEachHost),
        new("actions.parameterized.quoted", ParameterValuesCannotInjectShell),
        new("actions.sequence.stop-on-error", SequenceStopsAtTheFirstFailure),
        new("health-dashboard", HealthDashboardReadsARealHost),
        new("command-monitoring.change-fail-recover", MonitorAlertsOnChangeFailureAndRecovery),
        new("session-logs.record-search", SessionIsRecordedAndSearchable),
        new("broadcast.two-sessions", BroadcastReachesEverySession),
        new("free-tier.pro-features-refused", FreeTierIsRefusedProFeatures),
    ];

    /// <summary>A host nothing listens on (port 1 on the server's address), with the same saved key.</summary>
    private static Task<HostRecord> AddDeadHostAsync(E2EApp app, E2EContext c, HostRecord like) =>
        app.AddHostAsync("dead", c.Server.Host, 1, null, h => h with { CredentialId = like.CredentialId });

    private static async Task MultiHostActionReportsEachHost(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var dead = await AddDeadHostAsync(app, c, host);

        var actions = app.Get<ICommandActionService>();
        var action = new CommandAction(Guid.NewGuid(), "answer", "echo $((6*7)); exit 0", [host.Id, dead.Id], TimeoutSeconds: 20);
        await actions.SaveAsync(action);

        var run = await actions.RunAsync(action.Id);
        Assert.False(run.Succeeded);
        var ok = Assert.Single(run.Hosts, h => h.HostId == host.Id);
        Assert.Equal(CommandActionHostState.Succeeded, ok.State);
        Assert.Equal("42", ok.StandardOutput.Trim());
        // One unreachable host is reported as such, not as a failed command,
        // and does not take the others down with it.
        Assert.Equal(CommandActionHostState.ConnectionFailed, Assert.Single(run.Hosts, h => h.HostId == dead.Id).State);

        var failing = action with { Id = Guid.NewGuid(), Command = "echo partial; exit 5", HostIds = [host.Id] };
        await actions.SaveAsync(failing);
        var failed = Assert.Single((await actions.RunAsync(failing.Id)).Hosts);
        Assert.Equal(CommandActionHostState.Failed, failed.State);
        Assert.Equal(5, failed.ExitCode);
        Assert.Equal("partial", failed.StandardOutput.Trim());
    }

    private static async Task ParameterValuesCannotInjectShell(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var marker = $"/tmp/meow-e2e-injected-{Guid.NewGuid():N}";
        var action = new CommandAction(Guid.NewGuid(), "say", "printf '%s' {{message}}", [host.Id]);
        await app.Get<ICommandActionService>().SaveAsync(action);

        // Everything a hostile value could try: quotes, a command separator,
        // substitution. It must arrive as one literal argument.
        var hostile = $"it's $(id) `id`; touch {marker}";
        var run = await app.Get<IParameterizedCommandActionRunner>().RunAsync(action.Id, new Dictionary<string, string> { ["message"] = hostile });

        var result = Assert.Single(run.Hosts);
        Assert.Equal(CommandActionHostState.Succeeded, result.State);
        Assert.Equal(hostile, result.StandardOutput);
        await using var ssh = await app.ConnectSshAsync(host);
        Assert.Equal("absent", (await ssh.RunCommandAsync($"test -e {marker} && echo present || echo absent")).StandardOutput.Trim());
    }

    private static async Task SequenceStopsAtTheFirstFailure(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var dir = $"/tmp/meow-e2e-seq-{Guid.NewGuid():N}";
        var action = new CommandAction(Guid.NewGuid(), "deploy", $"mkdir -p {dir} && touch {dir}/1", [host.Id]);
        await app.Get<ICommandActionService>().SaveAsync(action);
        var sequences = app.Get<ICommandActionSequenceService>();
        await sequences.SaveAsync(new CommandActionSequence(action.Id, [$"touch {dir}/2", "exit 7", $"touch {dir}/4"], StopOnError: true));

        var run = await sequences.RunAsync(action.Id);
        Assert.False(run.Succeeded);

        await using var ssh = await app.ConnectSshAsync(host);
        // Steps 1 and 2 ran, step 3 failed, and step 4 never ran.
        Assert.Equal("1 2", (await ssh.RunCommandAsync($"cd {dir} && ls | sort | tr '\\n' ' '; rm -rf {dir}")).StandardOutput.Trim());
    }

    private static async Task HealthDashboardReadsARealHost(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var dead = await AddDeadHostAsync(app, c, host);
        var dashboard = app.Get<IHostHealthDashboardService>();

        var snapshot = await dashboard.CheckAsync(host.Id);
        Assert.Equal(HostHealthState.Healthy, snapshot.State);
        Assert.Contains("Linux", snapshot.OperatingSystem ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Uptime));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Disk));

        Assert.Equal(HostHealthState.Unreachable, (await dashboard.CheckAsync(dead.Id)).State);
        var all = await dashboard.CheckAllAsync();
        Assert.Equal(2, all.Count);
    }

    private static async Task MonitorAlertsOnChangeFailureAndRecovery(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var file = $"/tmp/meow-e2e-monitor-{Guid.NewGuid():N}";
        await using var ssh = await app.ConnectSshAsync(host);
        await ssh.RunCommandAsync($"echo v1 > {file}");

        var monitoring = app.Get<ICommandMonitoringService>();
        await monitoring.InitializeAsync();
        var monitor = new CommandMonitor(Guid.NewGuid(), "version file", host.Id, $"cat {file}", IntervalSeconds: 3600, TimeoutSeconds: 20);
        await monitoring.SaveAsync(monitor);
        var alerts = app.Get<RecordingCommandMonitorAlertSink>();

        try
        {
            var baseline = await monitoring.RunNowAsync(monitor.Id);
            Assert.Equal(CommandMonitorRunState.Healthy, baseline.State);
            Assert.Empty(alerts.Alerts);

            await ssh.RunCommandAsync($"echo v2 > {file}");
            var changed = await monitoring.RunNowAsync(monitor.Id);
            Assert.True(changed.OutputChanged);
            Assert.Equal(CommandMonitorAlertKind.OutputChanged, alerts.Alerts[^1].Kind);

            await ssh.RunCommandAsync($"rm {file}");
            Assert.Equal(CommandMonitorRunState.Failed, (await monitoring.RunNowAsync(monitor.Id)).State);
            Assert.Equal(CommandMonitorAlertKind.Failed, alerts.Alerts[^1].Kind);

            await ssh.RunCommandAsync($"echo v2 > {file}");
            Assert.Equal(CommandMonitorRunState.Healthy, (await monitoring.RunNowAsync(monitor.Id)).State);
            Assert.Equal(CommandMonitorAlertKind.Recovered, alerts.Alerts[^1].Kind);

            Assert.Equal(4, (await monitoring.GetHistoryAsync(monitor.Id)).Count);
        }
        finally
        {
            await ssh.RunCommandAsync($"rm -f {file}");
        }
    }

    private static async Task SessionIsRecordedAndSearchable(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        var logs = app.Get<ISessionLogService>();
        await logs.SetAutoRecordAsync(true);

        var marker = $"LOGGED_{Guid.NewGuid():N}"[..20];
        await using (var connection = await app.ConnectAsync(host))
        await using (var terminal = new E2ETerminal(await connection.OpenTerminalAsync(80, 24)))
        {
            await terminal.CheckAsync($"echo {marker}");
            await terminal.SendAsync("exit 3\n");
            await Task.WhenAny(terminal.ExitCode.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        SessionLogSummary? log = null;
        while (DateTime.UtcNow < deadline && (log = (await logs.GetAllAsync()).SingleOrDefault(l => l.EndedAtUtc is not null)) is null)
            await Task.Delay(200);
        Assert.NotNull(log);
        Assert.Equal(host.Id, log!.HostId);
        Assert.Contains(marker, await logs.ReadAsync(log.Id), StringComparison.Ordinal);
        Assert.Single(await logs.SearchAsync(marker));
        Assert.Empty(await logs.SearchAsync("NOT_IN_ANY_" + Guid.NewGuid().ToString("N")));

        await logs.DeleteAsync(log.Id);
        Assert.Empty(await logs.GetAllAsync());
    }

    private static async Task BroadcastReachesEverySession(E2EContext c)
    {
        var (app, host) = await c.StartWithKeyHostAsync();
        await using var first = await app.ConnectAsync(host);
        await using var second = await app.ConnectAsync(host);
        await using var a = new E2ETerminal(await first.OpenTerminalAsync(80, 24));
        await using var b = new E2ETerminal(await second.OpenTerminalAsync(80, 24));
        await a.CheckAsync("true");
        await b.CheckAsync("true");

        var result = await app.Get<ITerminalBroadcastService>().WriteAsync(
            [new TerminalBroadcastTarget(Guid.NewGuid(), a.Session), new TerminalBroadcastTarget(Guid.NewGuid(), b.Session)],
            "echo BCAST_$((40+2))\n"u8.ToArray());
        Assert.True(result.SucceededCompletely);

        await a.WaitForTextAsync("BCAST_42");
        await b.WaitForTextAsync("BCAST_42");
    }

    private static async Task FreeTierIsRefusedProFeatures(E2EContext c)
    {
        var app = await c.StartAppAsync(EntitlementTier.Free);
        var key = await app.AddPrivateKeyAsync(c.Server.Username, c.Server.ClientPrivateKey);
        var host = await app.AddHostAsync(c.Server, key);
        var other = await app.AddHostAsync(c.Server, key);

        // What stays free still works on Free...
        var single = new CommandAction(Guid.NewGuid(), "one", "echo free", [host.Id]);
        await app.Get<ICommandActionService>().SaveAsync(single);
        Assert.True((await app.Get<ICommandActionService>().RunAsync(single.Id)).Succeeded);

        // ...and each Pro feature refuses, rather than half-running.
        var multi = single with { Id = Guid.NewGuid(), HostIds = [host.Id, other.Id] };
        await app.Get<ICommandActionService>().SaveAsync(multi);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Get<ICommandActionService>().RunAsync(multi.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Get<IHostHealthDashboardService>().CheckAsync(host.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Get<ISessionLogService>().SetAutoRecordAsync(true));

        await using var first = await app.ConnectAsync(host);
        await using var second = await app.ConnectAsync(other);
        await using var a = await first.OpenTerminalAsync(80, 24);
        await using var b = await second.OpenTerminalAsync(80, 24);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Get<ITerminalBroadcastService>().WriteAsync(
            [new TerminalBroadcastTarget(Guid.NewGuid(), a), new TerminalBroadcastTarget(Guid.NewGuid(), b)], "x"u8.ToArray()));
    }
}
