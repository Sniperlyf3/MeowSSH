using MeowSSH.Core.Model;

namespace MeowSSH.Core.Ssh;

/// <summary>Opens SSH connections.</summary>
public interface ISshEngine
{
    Task<ISshConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A live SSH connection carrying shells, SFTP channels and forwards over one login.
/// </summary>
public interface ISshConnection : IHostConnection
{
    /// <summary>Latest live Tailcat path for this SSH connection, when available.</summary>
    new SshPathStatus? PathStatus => ((IHostConnection)this).PathStatus;

    /// <summary>Raised when the same live SSH connection changes between direct and relayed paths.</summary>
    new event EventHandler<SshPathStatus>? PathChanged
    {
        add => ((IHostConnection)this).PathChanged += value;
        remove => ((IHostConnection)this).PathChanged -= value;
    }

    Task<ISshShell> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default);

    async Task<ITerminalSession> IHostConnection.OpenTerminalAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken) =>
        await OpenShellAsync(columns, rows, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs a non-interactive command without allocating a pseudo-terminal and returns
    /// its complete stdout, stderr and exit code. Implementations must drain stdout and
    /// stderr concurrently so either stream can exceed the SSH channel window safely.
    /// </summary>
    Task<SshCommandResult> RunCommandAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default);

    Task<ISshForward> OpenLocalForwardAsync(
        string listenAddress,
        string remoteAddress,
        bool allowNonLoopbackBind = false,
        int maxConnections = 256,
        CancellationToken cancellationToken = default);

    Task<ISshForward> OpenLocalForwardOnUnixSocketAsync(
        string socketPath,
        string remoteAddress,
        int maxConnections = 256,
        CancellationToken cancellationToken = default);

    Task<ISshForward> OpenRemoteForwardAsync(
        string listenAddress,
        string localAddress,
        int maxConnections = 256,
        CancellationToken cancellationToken = default);

    Task<ISshForward> OpenSocksForwardAsync(
        string listenAddress,
        bool requireAuth = true,
        string? socksUsername = null,
        string? socksPassword = null,
        bool allowNonLoopbackBind = false,
        int maxConnections = 256,
        CancellationToken cancellationToken = default);

    Task<ISshForward> OpenSocksForwardOnUnixSocketAsync(
        string socketPath,
        bool requireAuth = false,
        string? socksUsername = null,
        string? socksPassword = null,
        int maxConnections = 256,
        CancellationToken cancellationToken = default);
}

/// <summary>The result of one non-interactive SSH command.</summary>
/// <param name="ExitCode">Remote process exit status.</param>
/// <param name="StandardOutput">UTF-8 decoded stdout.</param>
/// <param name="StandardError">UTF-8 decoded stderr.</param>
public sealed record SshCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>An interactive SSH shell with a pseudo-terminal.</summary>
public interface ISshShell : ITerminalSession;

/// <param name="Reason">Why the connection ended.</param>
/// <param name="Message">A line to show the user, written for a person.</param>
public sealed record SshConnectionLost(SshFailure Reason, string Message);

/// <summary>Informational path state for a live Tailcat-backed SSH connection.</summary>
/// <param name="Direct">True for peer-to-peer; false when traffic is currently using DERP.</param>
/// <param name="Relay">Relay region/name when known. Empty on a direct path.</param>
public sealed record SshPathStatus(bool Direct, string Relay)
{
    public string Label => Direct
        ? "Direct"
        : string.IsNullOrWhiteSpace(Relay) ? "Relayed" : $"Relayed · {Relay}";
}
