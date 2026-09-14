using MeowSSH.Core.Ssh;

namespace MeowSSH.UI.Components;

public sealed record ForwardRequest(
    SshForwardKind Kind,
    string ListenAddress,
    string? Destination,
    bool AllowNonLoopbackBind = false,
    bool RequireSocksAuth = true);

public sealed record ForwardInfo(
    Guid Id,
    SshForwardKind Kind,
    string BoundAddress,
    string? Destination,
    string? SocksUsername,
    string? SocksPassword);

public sealed record ForwardStopRequest(Guid Id);
