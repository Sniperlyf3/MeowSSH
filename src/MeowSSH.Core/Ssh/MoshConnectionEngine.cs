using System.Globalization;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using Meowshell;

namespace MeowSSH.Core.Ssh;

/// <summary>Opens roaming Mosh terminals bootstrapped through SSH.</summary>
public sealed class MoshConnectionEngine(MeowshellSshEngineOptions options) : IProtocolConnectionEngine
{
    public HostProtocol Protocol => HostProtocol.Mosh;

    public async Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(prompts);

        if (host.Transport != SshTransport.Tcp)
            throw new SshException(SshFailure.Unknown, "Mosh requires a directly reachable SSH host and UDP path.");

        EnsurePrivateWorkingDirectory(options.WorkingDirectory);

        var clientOptions = new TailcatClientOptions
        {
            HomeDirectory = options.WorkingDirectory,
            Timeout = options.ConnectTimeout,
            Verbose = options.Verbose,
            BinaryDirectory = options.BinaryDirectory!,
        };

        MeowshellMoshConnection? mosh = null;
        try
        {
            mosh = await MeowshellMoshConnection.ConnectAsync(
                clientOptions,
                destination: Destination(host, credentials),
                configure: Configure(credentials),
                port: host.Port.ToString(CultureInfo.InvariantCulture),
                knownHostsPath: options.KnownHostsPath,
                proxyUrl: options.ProxyUrl,
                configureConnection: connection => Attach(connection, prompts, credentials),
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new MoshHostConnection(host.Id, mosh);
        }
        catch (TailcatException ex)
        {
            if (mosh is not null) await mosh.DisposeAsync().ConfigureAwait(false);
            throw MeowshellSshEngine.Translate(ex);
        }
        catch (FileNotFoundException ex)
        {
            if (mosh is not null) await mosh.DisposeAsync().ConfigureAwait(false);
            throw new SshException(
                SshFailure.Unknown,
                "MeowSSH could not find the Mosh helper binaries it ships with. The app may be packaged incorrectly.",
                ex);
        }
        catch (IOException ex)
        {
            if (mosh is not null) await mosh.DisposeAsync().ConfigureAwait(false);
            throw new SshException(
                SshFailure.Unknown,
                $"MeowSSH could not set up local storage for the Mosh connection: {ex.Message}",
                ex);
        }
    }

    private MeowshellAgentConfigureOptions Configure(SshCredentials credentials) => new()
    {
        DisableLocalAgent = true,
        AllowLegacyKeyAlgorithms = options.AllowLegacyKeyAlgorithms,
        PrivateKeys = [.. credentials.PrivateKeys.Select(key => key.ReadOnlySpan.ToArray())],
        Certificates = [.. credentials.Certificates],
        KeystoreKeyIds = [.. credentials.KeyStoreKeyIds],
        KeystorePublicKeys = [.. credentials.KeyStorePublicKeys],
    };

    private static string Destination(HostRecord host, SshCredentials credentials)
    {
        var username = string.IsNullOrWhiteSpace(credentials.Username)
            ? host.Username
            : credentials.Username;
        return username is null ? host.Address : $"{username}@{host.Address}";
    }

    private static void Attach(
        MeowshellMoshConnection connection,
        ISshPrompts prompts,
        SshCredentials credentials)
    {
        connection.HostKeyPromptRequested += async (prompt, ct) =>
            await prompts.ConfirmUnknownHostKeyAsync(
                new HostKeyPrompt(prompt.Remote, prompt.Fingerprint, KeyType: ""), ct).ConfigureAwait(false);

        connection.PasswordRequested += async (prompt, ct) =>
        {
            if (credentials.Password is { } stored)
                return SecretToString(stored);

            using var secret = await prompts.RequestPasswordAsync(prompt, ct).ConfigureAwait(false);
            return SecretToString(secret);
        };

        connection.PassphraseRequested += async ct =>
        {
            if (credentials.KeyPassphrase is { } stored)
                return SecretToString(stored);

            using var secret = await prompts.RequestKeyPassphraseAsync(ct).ConfigureAwait(false);
            return SecretToString(secret);
        };

        connection.KeyboardInteractiveRequested += async (prompt, ct) =>
        {
            if (credentials.Password is { } stored
                && prompt.Questions.Count == 1
                && prompt.Echos.Count == 1
                && !prompt.Echos[0]
                && prompt.Questions[0].Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                return [SecretToString(stored)];
            }

            var answers = await prompts.AnswerChallengeAsync(
                new KeyboardInteractivePrompt(prompt.Name, prompt.Instruction, prompt.Questions, prompt.Echos),
                ct).ConfigureAwait(false);
            return answers is null ? [] : [.. answers];
        };
    }

    private static string SecretToString(SecretBuffer? secret) =>
        secret is null ? "" : System.Text.Encoding.UTF8.GetString(secret.ReadOnlySpan);

    private static void EnsurePrivateWorkingDirectory(string path)
    {
        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path, ownerOnly);
            return;
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode sharedAccess =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & sharedAccess) != 0) File.SetUnixFileMode(path, mode & ~sharedAccess);
    }

    private sealed class MoshHostConnection : IHostConnection
    {
        private readonly MeowshellMoshConnection _mosh;
        private readonly MoshTerminalSession _terminal;
        private bool _disposed;

        public MoshHostConnection(Guid hostId, MeowshellMoshConnection mosh)
        {
            HostId = hostId;
            _mosh = mosh;
            _terminal = new MoshTerminalSession(mosh);
            _mosh.ConnectionLost += OnConnectionLost;
        }

        public Guid HostId { get; }
        public bool IsConnected => !_disposed && _mosh.IsConnected;

        public SshPathStatus? PathStatus => null;
        public event EventHandler<SshPathStatus>? PathChanged
        {
            add { }
            remove { }
        }
        public event EventHandler<SshConnectionLost>? ConnectionLost;

        public async Task<ITerminalSession> OpenTerminalAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _mosh.ResizeAsync(columns, rows, cancellationToken).ConfigureAwait(false);
            return _terminal;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _mosh.ConnectionLost -= OnConnectionLost;
            await _terminal.DisposeAsync().ConfigureAwait(false);
            await _mosh.DisposeAsync().ConfigureAwait(false);
        }

        private void OnConnectionLost(object? sender, Exception ex)
        {
            var translated = ex is TailcatException tailcat
                ? MeowshellSshEngine.Translate(tailcat)
                : new SshException(SshFailure.ConnectionLost, "The Mosh session ended unexpectedly.", ex);
            ConnectionLost?.Invoke(this, new SshConnectionLost(translated.Failure, translated.Message));
            _terminal.NotifyExited();
        }
    }

    private sealed class MoshTerminalSession : ITerminalSession
    {
        private readonly MeowshellMoshConnection _mosh;
        private bool _disposed;

        public MoshTerminalSession(MeowshellMoshConnection mosh)
        {
            _mosh = mosh;
            _mosh.OutputReceived += OnOutputReceived;
        }

        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public event EventHandler<int>? Exited;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _mosh.WriteAsync(data, cancellationToken);
        }

        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _mosh.ResizeAsync(columns, rows, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _mosh.OutputReceived -= OnOutputReceived;
            return ValueTask.CompletedTask;
        }

        public void NotifyExited()
        {
            if (!_disposed) Exited?.Invoke(this, 1);
        }

        private void OnOutputReceived(object? sender, ReadOnlyMemory<byte> data) =>
            OutputReceived?.Invoke(this, data);
    }
}
