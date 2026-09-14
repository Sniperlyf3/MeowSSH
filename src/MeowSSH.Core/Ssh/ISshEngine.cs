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
    Task<ISshShell> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default);

    async Task<ITerminalSession> IHostConnection.OpenTerminalAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken) =>
        await OpenShellAsync(columns, rows, cancellationToken).ConfigureAwait(false);

    Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default);

    Task<ISshForward> OpenLocalForwardAsync(
        string listenAddress,
        string remoteAddress,
        bool allowNonLoopbackBind = false,
        CancellationToken cancellationToken = default);

    Task<ISshForward> OpenRemoteForwardAsync(
        string listenAddress,
        string localAddress,
        CancellationToken cancellationToken = default);

    Task<ISshForward> OpenSocksForwardAsync(
        string listenAddress,
        bool requireAuth = true,
        CancellationToken cancellationToken = default);
}

/// <summary>An interactive SSH shell with a pseudo-terminal.</summary>
public interface ISshShell : ITerminalSession;

/// <param name="Reason">Why the connection ended.</param>
/// <param name="Message">A line to show the user, written for a person.</param>
public sealed record SshConnectionLost(SshFailure Reason, string Message);
