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
        .. VaultScenarios.All,
        .. TailcatScenarios.All,
    ];

    /// <summary>
    /// The app and MeowSSHAPI together. Kept out of <see cref="All"/>: they
    /// need a running API, which only MeowSSHAPI's CI has (see <see cref="E2EApi"/>).
    /// </summary>
    public static IReadOnlyList<E2EScenario> Api { get; } = ApiScenarios.All;

    public static E2EScenario Named(string name) =>
        All.Concat(Api).SingleOrDefault(s => s.Name == name) ?? throw new ArgumentException($"No E2E scenario '{name}'.", nameof(name));
}
