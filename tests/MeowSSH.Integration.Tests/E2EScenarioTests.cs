using System.Runtime.Versioning;
using MeowSSH.E2E;
using Xunit.Abstractions;

namespace MeowSSH.Integration.Tests;

/// <summary>
/// The shared end-to-end scenarios on Linux, against this suite's real sshd.
/// </summary>
/// <remarks>
/// The same scenarios run on the Android emulator (MeowSSH.Device.Tests), which
/// is the run that covers the Keystore and the Android engine binaries. This
/// one is the quick loop, and catches a scenario that is wrong on its own
/// terms before an emulator boot is spent finding out.
/// </remarks>
[Collection(nameof(SshServerCollection))]
[SupportedOSPlatform("linux")]
public sealed class E2EScenarioTests(SshServerFixture server, ITestOutputHelper output)
{
    public static TheoryData<string> Scenarios => [.. E2EScenarios.All.Select(s => s.Name)];

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario(string name)
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");

        // E2E_HOST etc., as scripts/start-e2e-sshd.sh exports them, point the run
        // at that server instead: the way to check the CI server setup itself.
        var target = E2EServer.From(key => Environment.GetEnvironmentVariable(key.ToUpperInvariant()))
            ?? server.AsE2EServer();
        var platform = new SoftwareE2EPlatform(server.WorkspaceFor("e2e"));
        var result = await E2ERunner.RunAsync(E2EScenarios.Named(name), platform, target, output.WriteLine);

        output.WriteLine($"{result.Name}: {(result.Passed ? "passed" : "FAILED")} in {result.Duration.TotalSeconds:0.0}s");
        Assert.True(result.Passed, result.Failure);
    }
}
