using MeowSSH.Core.Model;

namespace MeowSSH.Core.Ssh;

/// <summary>Opens SSH connections.</summary>
/// <remarks>
/// An interface rather than a direct dependency on Meowshell for one practical
/// reason: the engine runs a subprocess and speaks to a real server, neither of
/// which exists on a CI machine driving the UI. Everything above this line is
/// testable without either.
/// </remarks>
public interface ISshEngine
{
    /// <summary>
    /// Connects to <paramref name="host"/> offering <paramref name="credentials"/>,
    /// calling back into <paramref name="prompts"/> for anything only the user can
    /// answer.
    /// </summary>
    /// <exception cref="SshException">The connection could not be established.</exception>
    Task<ISshConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A live connection to one host, carrying however many shells, transfers and
/// forwards the user opens on it.
/// </summary>
/// <remarks>
/// One connection, many channels. A user who opens a shell and a file browser on
/// the same host should authenticate once — not once per feature, which with a
/// one-time code would mean fishing out their phone twice.
/// </remarks>
public interface ISshConnection : IAsyncDisposable
{
    Guid HostId { get; }

    bool IsConnected { get; }

    Task<ISshShell> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default);

    /// <summary>Raised when the connection drops without being closed deliberately.</summary>
    event EventHandler<SshConnectionLost>? ConnectionLost;
}

/// <summary>An interactive shell with a pseudo-terminal.</summary>
public interface ISshShell : IAsyncDisposable
{
    /// <summary>
    /// Raised for each chunk of terminal output. Chunks are whatever the network
    /// delivered, so a single escape sequence can be split across two of them —
    /// the terminal emulator reassembles, callers must not try to.
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;

    /// <summary>Raised when the remote shell exits.</summary>
    event EventHandler<int>? Exited;

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the remote end the terminal changed size, so full-screen programs
    /// redraw correctly.
    /// </summary>
    /// <remarks>
    /// Called far more often on a phone than on a desktop: every rotation, and
    /// every time the soft keyboard appears or disappears.
    /// </remarks>
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
}

/// <param name="Reason">Why the connection ended.</param>
/// <param name="Message">A line to show the user, written for a person.</param>
public sealed record SshConnectionLost(SshFailure Reason, string Message);
