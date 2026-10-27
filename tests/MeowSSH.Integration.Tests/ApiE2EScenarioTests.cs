using System.Runtime.Versioning;
using MeowSSH.E2E;
using Xunit.Abstractions;

namespace MeowSSH.Integration.Tests;

/// <summary>
/// The app against a real MeowSSHAPI: run by MeowSSHAPI's CI, which starts the
/// API and sets E2E_API_URL and E2E_API_CERT_SHA256. Skipped without them, as
/// in this repository's own CI.
/// </summary>
[Collection(nameof(SshServerCollection))]
[SupportedOSPlatform("linux")]
public sealed class ApiE2EScenarioTests(SshServerFixture server, ITestOutputHelper output)
{
    public static TheoryData<string> Scenarios => [.. E2EScenarios.Api.Select(s => s.Name)];

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario(string name)
    {
        var api = E2EApi.From(key => Environment.GetEnvironmentVariable(key.ToUpperInvariant()));
        Skip.If(api is null, "No MeowSSHAPI to run against (E2E_API_URL unset).");
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");

        var target = E2EServer.From(key => Environment.GetEnvironmentVariable(key.ToUpperInvariant()))
            ?? server.AsE2EServer();
        var platform = new SoftwareE2EPlatform(server.WorkspaceFor("e2e-api"));
        var result = await E2ERunner.RunAsync(E2EScenarios.Named(name), platform, target, output.WriteLine, api: api);

        output.WriteLine($"{result.Name}: {(result.Passed ? "passed" : "FAILED")} in {result.Duration.TotalSeconds:0.0}s");
        Assert.True(result.Passed, result.Failure);
    }
}
