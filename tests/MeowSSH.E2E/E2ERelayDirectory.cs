using MeowSSH.Core.Services;

namespace MeowSSH.E2E;

/// <summary>
/// The relay directory a scenario controls. Every run is on Tailcat's loopback
/// relay (a node named "T" on 127.0.0.1, embedded in the address), and by
/// default that is a relay nobody claims -- a custom one, used without asking.
/// A scenario labels it MeowSSH's or the public one to put a real connection
/// through each branch of the relay policy.
/// </summary>
public sealed class E2ERelayDirectory : ITailcatRelayDirectorySource
{
    /// <summary>The managed map every E2E install is built with: Tailcat's "none".</summary>
    public const string ManagedDerpMapUrl = "none";

    /// <summary>The loopback relay's host name, as the relay policy normalises it.</summary>
    public const string LoopbackRelayHost = "t";

    private TailcatRelayDirectory _current = Empty;

    private static TailcatRelayDirectory Empty => new(new HashSet<long>(), new HashSet<string>(), new HashSet<string>());

    public void TreatLoopbackAsManaged() => _current = Empty with { ManagedRelayHosts = new HashSet<string> { LoopbackRelayHost } };

    public void TreatLoopbackAsPublic() => _current = Empty with { PublicRelayHosts = new HashSet<string> { LoopbackRelayHost } };

    public void TreatLoopbackAsCustom() => _current = Empty;

    public Task<TailcatRelayDirectory> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_current);
}
