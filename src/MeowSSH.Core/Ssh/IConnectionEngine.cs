using MeowSSH.Core.Model;

namespace MeowSSH.Core.Ssh;

/// <summary>Connects any terminal protocol supported by MeowSSH.</summary>
public interface IConnectionEngine
{
    Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default);
}

/// <summary>A live endpoint that can open an interactive terminal.</summary>
public interface IHostConnection : IAsyncDisposable
{
    Guid HostId { get; }
    bool IsConnected { get; }

    Task<ITerminalSession> OpenTerminalAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default);

    event EventHandler<SshConnectionLost>? ConnectionLost;
}

/// <summary>Raw terminal bytes plus resize and exit notifications.</summary>
public interface ITerminalSession : IAsyncDisposable
{
    event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    event EventHandler<int>? Exited;

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
}

/// <summary>Protocol-specific engine used by the connection router.</summary>
public interface IProtocolConnectionEngine
{
    HostProtocol Protocol { get; }

    Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default);
}

/// <summary>Dispatches a saved endpoint to the engine for its selected protocol.</summary>
public sealed class ConnectionEngine(IEnumerable<IProtocolConnectionEngine> engines) : IConnectionEngine
{
    private readonly Dictionary<HostProtocol, IProtocolConnectionEngine> _engines =
        engines.ToDictionary(engine => engine.Protocol);

    public Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        if (!_engines.TryGetValue(host.Protocol, out var engine))
            throw new SshException(
                SshFailure.Unknown,
                $"{host.Protocol} connections are not available on this device.");

        return engine.ConnectAsync(host, credentials, prompts, cancellationToken);
    }
}

/// <summary>Adapts the existing SSH engine to the protocol-neutral connection router.</summary>
public sealed class SshProtocolConnectionEngine(ISshEngine ssh) : IProtocolConnectionEngine
{
    public HostProtocol Protocol => HostProtocol.Ssh;

    public async Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default) =>
        await ssh.ConnectAsync(host, credentials, prompts, cancellationToken).ConfigureAwait(false);
}
