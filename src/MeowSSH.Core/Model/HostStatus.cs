namespace MeowSSH.Core.Model;

/// <summary>A host paired with what the app currently knows about reaching it.</summary>
/// <param name="Host">The saved host.</param>
/// <param name="State">Where its connection stands right now.</param>
/// <param name="Detail">
/// One short line explaining the state — a latency, or why it failed. Shown
/// beside the host, so it has to be readable rather than a raw error string.
/// </param>
public sealed record HostStatus(HostRecord Host, ConnectionState State = ConnectionState.Disconnected, string? Detail = null);
