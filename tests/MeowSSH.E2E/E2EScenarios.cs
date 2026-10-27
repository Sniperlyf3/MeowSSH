using MeowSSH.E2E.Scenarios;

namespace MeowSSH.E2E;

/// <summary>Every scenario, in the order a run executes them.</summary>
public static class E2EScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        .. SshScenarios.All,
        .. SftpScenarios.All,
        .. ForwardingScenarios.All,
        .. ProtocolScenarios.All,
        .. AutomationScenarios.All,
    ];

    public static E2EScenario Named(string name) =>
        All.SingleOrDefault(s => s.Name == name) ?? throw new ArgumentException($"No E2E scenario '{name}'.", nameof(name));
}
