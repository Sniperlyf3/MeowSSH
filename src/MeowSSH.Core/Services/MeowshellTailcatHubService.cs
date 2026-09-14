using Meowshell;

namespace MeowSSH.Core.Services;

public sealed record TailcatHubRuntimeOptions(
    string BinaryDirectory,
    string HomeDirectory,
    string WorkDirectory,
    string? DerpMapUrl = null);

/// <summary>Owns Tailcat listeners and one-shot client operations for the application process.</summary>
public sealed class MeowshellTailcatHubService : ITailcatHubService
{
    private const int MaxLogLines = 100;
    private readonly TailcatHubRuntimeOptions _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly List<string> _logs = [];
    private readonly List<RunningForward> _forwards = [];
    private MeowshellServer? _server;
    private TailcatServerSnapshot? _serverSnapshot;
    private MeowshellSocksProxy? _socks;
    private TailcatSocksSnapshot? _socksSnapshot;
    private bool _disposed;

    private sealed record RunningForward(Guid Id, TailcatForwardSnapshot Snapshot, MeowshellPortForward Forward);

    public MeowshellTailcatHubService(TailcatHubRuntimeOptions runtime)
    {
        _runtime = runtime;
    }

    public event EventHandler? Changed;

    public TailcatHubSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
            {
                return new TailcatHubSnapshot(
                    _serverSnapshot,
                    _socksSnapshot,
                    [.. _forwards.Select(forward => forward.Snapshot)],
                    [.. _logs]);
            }
        }
    }

    public async Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateServerRequest(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_server is not null) throw new InvalidOperationException("Tailcat sharing is already running.");

            var files = request.EnableFiles
                ? $"{request.SharedFolder!.Trim()}:{request.FileMode}"
                : null;
            var options = new MeowshellOptions
            {
                BinaryDirectory = _runtime.BinaryDirectory,
                HomeDirectory = _runtime.HomeDirectory,
                WorkDirectory = _runtime.WorkDirectory,
                DerpMapUrl = _runtime.DerpMapUrl,
                Lifetime = request.Lifetime,
                AllowClientKeys = request.AllowAnyClient ? null : request.AllowedClientKeys.Trim(),
                AuthorizedKeys = request.EnableShell && !request.InsecureShell ? request.AuthorizedSshKeys!.Trim() : null,
                InsecureNoAuth = request.EnableShell && request.InsecureShell,
                Files = files,
                AllowExitNode = request.EnableExitNode,
                FullAddress = request.FullAddress,
                Psk = request.UsePresharedKey,
                EphemeralKey = true,
            };

            var server = await MeowshellServer.StartAsync(options, cancellationToken, AddLog).ConfigureAwait(false);
            _server = server;
            _serverSnapshot = new TailcatServerSnapshot(
                server.Address,
                server.ExpiresAt,
                request.EnableShell,
                request.EnableFiles,
                request.EnableExitNode,
                request.AllowAnyClient,
                request.EnableFiles ? request.SharedFolder!.Trim() : null,
                request.FileMode);
            server.Log += AddLog;
            _ = ObserveServerAsync(server);
        }
        finally
        {
            _gate.Release();
        }
        RaiseChanged();
    }

    public async Task StopServerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MeowshellServer? server;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            server = _server;
            _server = null;
            lock (_stateGate) _serverSnapshot = null;
        }
        finally
        {
            _gate.Release();
        }

        if (server is not null)
        {
            server.Log -= AddLog;
            await server.DisposeAsync().ConfigureAwait(false);
        }
        RaiseChanged();
    }

    public async Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_socks is not null) throw new InvalidOperationException("The Tailcat SOCKS gateway is already running.");
            if (string.IsNullOrWhiteSpace(request.ListenAddress)) throw new ArgumentException("A listen address is required.", nameof(request));

            var proxy = await MeowshellSocksProxy.StartAsync(new MeowshellSocksOptions
            {
                BinaryDirectory = _runtime.BinaryDirectory,
                HomeDirectory = _runtime.HomeDirectory,
                DerpMapUrl = _runtime.DerpMapUrl,
                Listen = request.ListenAddress.Trim(),
                ClientKey = NullIfWhiteSpace(request.ClientKey),
            }, AddLog, cancellationToken).ConfigureAwait(false);
            _socks = proxy;
            lock (_stateGate) _socksSnapshot = new TailcatSocksSnapshot(proxy.ListenAddress, NullIfWhiteSpace(request.ClientKey));
            proxy.Log += AddLog;
            _ = ObserveSocksAsync(proxy);
        }
        finally
        {
            _gate.Release();
        }
        RaiseChanged();
    }

    public async Task StopSocksAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MeowshellSocksProxy? socks;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            socks = _socks;
            _socks = null;
            lock (_stateGate) _socksSnapshot = null;
        }
        finally
        {
            _gate.Release();
        }
        if (socks is not null)
        {
            socks.Log -= AddLog;
            await socks.DisposeAsync().ConfigureAwait(false);
        }
        RaiseChanged();
    }

    public async Task<TailcatForwardSnapshot> StartForwardAsync(TailcatForwardRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Address)) throw new ArgumentException("A Tailcat address is required.", nameof(request));
        if (request.Mappings.Count == 0 || request.Mappings.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty mapping is required.", nameof(request));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var native = await MeowshellPortForward.StartAsync(new MeowshellPortForwardOptions
            {
                BinaryDirectory = _runtime.BinaryDirectory,
                HomeDirectory = _runtime.HomeDirectory,
                DerpMapUrl = _runtime.DerpMapUrl,
                Address = request.Address.Trim(),
                Mappings = [.. request.Mappings.Select(mapping => mapping.Trim())],
                Bind = NullIfWhiteSpace(request.BindAddress),
                ClientKey = NullIfWhiteSpace(request.ClientKey),
            }, AddLog, cancellationToken).ConfigureAwait(false);
            native.Log += AddLog;
            var snapshot = new TailcatForwardSnapshot(
                Guid.NewGuid(), request.Address.Trim(),
                [.. request.Mappings.Select(mapping => mapping.Trim())],
                native.BoundAddresses,
                NullIfWhiteSpace(request.ClientKey));
            lock (_stateGate) _forwards.Add(new RunningForward(snapshot.Id, snapshot, native));
            _ = ObserveForwardAsync(snapshot.Id, native);
            RaiseChanged();
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunningForward? running;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                running = _forwards.FirstOrDefault(forward => forward.Id == id);
                if (running is not null) _forwards.Remove(running);
            }
        }
        finally
        {
            _gate.Release();
        }
        if (running is not null)
        {
            running.Forward.Log -= AddLog;
            await running.Forward.DisposeAsync().ConfigureAwait(false);
            RaiseChanged();
        }
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TailcatClient.ListKeysAsync(ClientOptions());
    }

    public async Task<TailcatGeneratedKey> GenerateKeyAsync(TailcatGenerateKeyRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("A key name is required.", nameof(request));
        var value = await TailcatClient.GenerateKeyAsync(ClientOptions(), new TailcatKeyOptions
        {
            Name = request.Name.Trim(),
            Client = request.Client,
            Region = NullIfWhiteSpace(request.Region),
            FixedRegion = request.FixedRegion,
            EmbedDerpMap = request.EmbedDerpMap,
            Psk = request.UsePresharedKey,
            Force = request.Overwrite,
        }).ConfigureAwait(false);
        RaiseChanged();
        return new TailcatGeneratedKey(request.Name.Trim(), request.Client, value);
    }

    public async Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A key name is required.", nameof(name));
        await TailcatClient.DeleteKeyAsync(ClientOptions(), name.Trim()).ConfigureAwait(false);
        RaiseChanged();
    }

    public Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TailcatClient.PrintPubAsync(ClientOptions(), NullIfWhiteSpace(name));
    }

    public async Task<TailcatDiagnosticResult> DiagnoseAsync(string address, bool waitForDirect, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("A Tailcat address or DNS name is required.", nameof(address));
        var options = ClientOptions();
        var resolved = await TailcatClient.ResolveAsync(options, address.Trim()).ConfigureAwait(false);
        var parsed = await TailcatClient.ParseAsync(options, resolved).ConfigureAwait(false);
        var ping = await TailcatClient.PingAsync(options, resolved.ToString(), waitForDirect, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var region = parsed.Region is { Count: > 0 } regions ? regions[0] : null;
        return new TailcatDiagnosticResult(
            resolved.ToString(), ping.Success,
            ping.Pong?.Latency, ping.Pong?.Direct, ping.Pong?.Via,
            parsed.RegionId,
            region?.RegionName ?? region?.RegionCode,
            parsed.ServerPublic,
            !string.IsNullOrWhiteSpace(parsed.PresharedKey));
    }

    public async Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(string address, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = RemotePath(address, path);
        var entries = await TailcatClient.ListFilesAsync(ClientOptions(), target, longListing: true).ConfigureAwait(false);
        return [.. entries.Select(entry => new TailcatRemoteFile(entry.Name, entry.IsDirectory, entry.Mode, entry.Size, entry.ModifiedAt))];
    }

    public async Task UploadAsync(string localPath, string address, string remotePath, bool recursive = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLocalPath(localPath);
        var result = await TailcatClient.CpAsync(
            ClientOptions(TimeSpan.FromMinutes(10)),
            TailcatPath.Local(localPath.Trim()), RemotePath(address, remotePath), recursive).ConfigureAwait(false);
        if (!result.Success) throw new TailcatException("Tailcat upload failed", result.ExitCode, result.Stderr);
    }

    public async Task DownloadAsync(string address, string remotePath, string localPath, bool recursive = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLocalPath(localPath);
        var result = await TailcatClient.CpAsync(
            ClientOptions(TimeSpan.FromMinutes(10)),
            RemotePath(address, remotePath), TailcatPath.Local(localPath.Trim()), recursive).ConfigureAwait(false);
        if (!result.Success) throw new TailcatException("Tailcat download failed", result.ExitCode, result.Stderr);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopSocksAsync().ConfigureAwait(false);
        TailcatForwardSnapshot[] forwards;
        lock (_stateGate) forwards = [.. _forwards.Select(forward => forward.Snapshot)];
        foreach (var forward in forwards) await StopForwardAsync(forward.Id).ConfigureAwait(false);
        await StopServerAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private TailcatClientOptions ClientOptions(TimeSpan? timeout = null) => new()
    {
        BinaryDirectory = _runtime.BinaryDirectory,
        HomeDirectory = _runtime.HomeDirectory,
        DerpMapUrl = _runtime.DerpMapUrl,
        Timeout = timeout ?? TimeSpan.FromSeconds(30),
    };

    private static TailcatPath RemotePath(string address, string? path)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("A Tailcat address or DNS name is required.", nameof(address));
        var trimmed = address.Trim();
        var remotePath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        return trimmed.StartsWith("tc", StringComparison.Ordinal)
            ? TailcatPath.Remote(new TailcatAddress(trimmed), remotePath)
            : TailcatPath.RemoteHost(trimmed, remotePath);
    }

    private static void ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A local path is required.", nameof(path));
    }

    private static void ValidateServerRequest(TailcatServeRequest request)
    {
        if (request.Lifetime <= TimeSpan.Zero || request.Lifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(request), "Tailcat sharing lifetime must be between 1 second and 24 hours.");
        if (!request.EnableShell && !request.EnableFiles && !request.EnableExitNode)
            throw new ArgumentException("Enable at least one Tailcat service.", nameof(request));
        if (request.AllowAnyClient && !request.EnableExitNode)
            throw new ArgumentException("Insecure Tailcat client mode is only available when exit-node mode is enabled.", nameof(request));
        if (!request.AllowAnyClient)
        {
            if (string.IsNullOrWhiteSpace(request.AllowedClientKeys))
                throw new ArgumentException("At least one allowed Tailcat client key is required unless insecure mode is explicitly enabled.", nameof(request));
            var keys = request.AllowedClientKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keys.Length == 0 || keys.Any(key => !key.StartsWith("nodekey:", StringComparison.Ordinal)))
                throw new ArgumentException("Allowed clients must be comma-separated nodekey: public keys.", nameof(request));
        }
        if (request.EnableShell && string.IsNullOrWhiteSpace(request.AuthorizedSshKeys))
            throw new ArgumentException("Shell sharing requires at least one authorized SSH key.", nameof(request));
        if (request.EnableFiles && string.IsNullOrWhiteSpace(request.SharedFolder))
            throw new ArgumentException("File sharing requires a folder path.", nameof(request));
        if (request.FileMode is not ("ro" or "rw" or "wo" or "wo+"))
            throw new ArgumentException("File mode must be ro, rw, wo, or wo+.", nameof(request));
    }

    private void AddLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_stateGate)
        {
            _logs.Add(line.Trim());
            if (_logs.Count > MaxLogLines) _logs.RemoveRange(0, _logs.Count - MaxLogLines);
        }
        RaiseChanged();
    }

    private async Task ObserveServerAsync(MeowshellServer server)
    {
        try { await server.Completed.ConfigureAwait(false); }
        catch (Exception ex) { AddLog($"Tailcat server stopped: {ex.Message}"); }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_server, server))
                {
                    _server = null;
                    _serverSnapshot = null;
                }
            }
            RaiseChanged();
        }
    }

    private async Task ObserveSocksAsync(MeowshellSocksProxy socks)
    {
        try { await socks.Completed.ConfigureAwait(false); }
        catch (Exception ex) { AddLog($"Tailcat SOCKS gateway stopped: {ex.Message}"); }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_socks, socks))
                {
                    _socks = null;
                    _socksSnapshot = null;
                }
            }
            RaiseChanged();
        }
    }

    private async Task ObserveForwardAsync(Guid id, MeowshellPortForward forward)
    {
        try { await forward.Completed.ConfigureAwait(false); }
        catch (Exception ex) { AddLog($"Tailcat forward stopped: {ex.Message}"); }
        finally
        {
            lock (_stateGate) _forwards.RemoveAll(item => item.Id == id && ReferenceEquals(item.Forward, forward));
            RaiseChanged();
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
