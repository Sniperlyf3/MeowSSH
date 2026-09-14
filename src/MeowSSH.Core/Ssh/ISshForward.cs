namespace MeowSSH.Core.Ssh;

public enum SshForwardKind
{
    Local,
    Remote,
    Socks
}

/// <summary>A running SSH port forward on an existing authenticated connection.</summary>
public interface ISshForward : IAsyncDisposable
{
    SshForwardKind Kind { get; }

    /// <summary>The listener address actually bound by the SSH client/server.</summary>
    string BoundAddress { get; }

    /// <summary>Destination for local/remote forwards; null for SOCKS.</summary>
    string? Destination { get; }

    /// <summary>Generated or configured SOCKS username, when authentication is enabled.</summary>
    string? SocksUsername { get; }

    /// <summary>Generated or configured SOCKS password, when authentication is enabled.</summary>
    string? SocksPassword { get; }

    Task CloseAsync(CancellationToken cancellationToken = default);
}
