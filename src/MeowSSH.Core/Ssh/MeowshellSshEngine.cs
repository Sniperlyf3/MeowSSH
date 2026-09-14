using System.Globalization;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using Meowshell;

namespace MeowSSH.Core.Ssh;

/// <summary>
/// The real engine: <see cref="ISshEngine"/> over Meowshell's agent, which runs
/// one long-lived subprocess per connection and multiplexes every channel over it.
/// </summary>
/// <remarks>
/// One agent per host, not per feature. A user who opens a shell and then browses
/// files should authenticate once — with a one-time code, per-feature connections
/// would mean reaching for their phone again for each.
/// </remarks>
public sealed class MeowshellSshEngine(MeowshellSshEngineOptions options) : ISshEngine
{
    public async Task<ISshConnection> ConnectAsync(
        HostRecord host, SshCredentials credentials, ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(prompts);

        EnsurePrivateWorkingDirectory(options.WorkingDirectory);

        var clientOptions = new TailcatClientOptions
        {
            HomeDirectory = options.WorkingDirectory,
            Timeout = options.ConnectTimeout,
            Verbose = options.Verbose,
            BinaryDirectory = options.BinaryDirectory!,
        };

        MeowshellAgentConnection? agent = null;
        try
        {
            agent = await MeowshellAgentConnection.ConnectAsync(
                clientOptions,
                destination: Destination(host, credentials),
                configure: Configure(credentials),
                port: host.Transport == SshTransport.Tailcat
                    ? null
                    : host.Port.ToString(CultureInfo.InvariantCulture),
                jumpHosts: null,
                knownHostsPath: options.KnownHostsPath,
                proxyUrl: options.ProxyUrl,
                configureConnection: connection => Attach(connection, prompts, credentials),
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new MeowshellSshConnection(host.Id, agent);
        }
        catch (TailcatException ex)
        {
            if (agent is not null) await agent.DisposeAsync().ConfigureAwait(false);
            throw Translate(ex);
        }
        catch (FileNotFoundException ex)
        {
            if (agent is not null) await agent.DisposeAsync().ConfigureAwait(false);
            throw new SshException(
                SshFailure.Unknown,
                "MeowSSH could not find the SSH helper binaries it ships with. The app may be packaged incorrectly.",
                ex);
        }
        catch (IOException ex)
        {
            if (agent is not null) await agent.DisposeAsync().ConfigureAwait(false);
            throw new SshException(
                SshFailure.Unknown,
                $"MeowSSH could not set up local storage for the connection: {ex.Message}",
                ex);
        }
    }

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

    private MeowshellAgentConfigureOptions Configure(SshCredentials credentials) => new()
    {
        DisableLocalAgent = true,
        AllowLegacyKeyAlgorithms = options.AllowLegacyKeyAlgorithms,
        PrivateKeys = [.. credentials.PrivateKeys.Select(k => k.ReadOnlySpan.ToArray())],
        Certificates = [.. credentials.Certificates],
        KeystoreKeyIds = [.. credentials.KeyStoreKeyIds],
        KeystorePublicKeys = [.. credentials.KeyStorePublicKeys],
    };

    private static string Destination(HostRecord host, SshCredentials credentials)
    {
        if (host.Transport == SshTransport.Tailcat) return host.Address;

        // A username stored with the selected credential belongs to that
        // credential and therefore takes precedence over the host-level default.
        // This lets one credential carry the complete login identity.
        var username = string.IsNullOrWhiteSpace(credentials.Username)
            ? host.Username
            : credentials.Username;
        return username is null ? host.Address : $"{username}@{host.Address}";
    }

    private static void Attach(
        MeowshellAgentConnection agent,
        ISshPrompts prompts,
        SshCredentials credentials)
    {
        agent.HostKeyPromptRequested += async (prompt, ct) =>
            await prompts.ConfirmUnknownHostKeyAsync(
                new HostKeyPrompt(prompt.Remote, prompt.Fingerprint, KeyType: ""), ct).ConfigureAwait(false);

        agent.PasswordRequested += async (prompt, ct) =>
        {
            if (credentials.Password is { } stored)
                return SecretToString(stored);

            using var secret = await prompts.RequestPasswordAsync(prompt, ct).ConfigureAwait(false);
            return SecretToString(secret);
        };

        agent.PassphraseRequested += async ct =>
        {
            if (credentials.KeyPassphrase is { } stored)
                return SecretToString(stored);

            using var secret = await prompts.RequestKeyPassphraseAsync(ct).ConfigureAwait(false);
            return SecretToString(secret);
        };

        agent.KeyboardInteractiveRequested += async (prompt, ct) =>
        {
            // PAM-backed SSH servers often expose an ordinary password through
            // keyboard-interactive rather than the SSH password method. Use the
            // stored password only for the unambiguous single hidden password
            // question; OTP/MFA challenges must remain interactive.
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

    internal static SshException Translate(TailcatException ex) => new(
        ex.Code switch
        {
            MeowshellErrorCode.AuthFailed => SshFailure.AuthenticationFailed,
            MeowshellErrorCode.HostKeyUnknown => SshFailure.HostKeyUnknown,
            MeowshellErrorCode.HostKeyChanged => SshFailure.HostKeyChanged,
            MeowshellErrorCode.NetworkUnreachable => SshFailure.NetworkUnreachable,
            MeowshellErrorCode.Timeout => SshFailure.Timeout,
            MeowshellErrorCode.ConnectionLost => SshFailure.ConnectionLost,
            MeowshellErrorCode.PermissionDenied => SshFailure.PermissionDenied,
            MeowshellErrorCode.NotFound => SshFailure.NotFound,
            MeowshellErrorCode.Cancelled => SshFailure.Cancelled,
            _ => SshFailure.Unknown,
        },
        Describe(ex),
        ex);

    private static string Describe(TailcatException ex) => ex.Code switch
    {
        MeowshellErrorCode.AuthFailed => "The server refused these credentials.",
        MeowshellErrorCode.HostKeyUnknown => "This server has not been trusted yet.",
        MeowshellErrorCode.HostKeyChanged =>
            "This server is presenting a different key than the one MeowSSH recorded. "
            + "Either it was rebuilt, or something is intercepting the connection.",
        MeowshellErrorCode.NetworkUnreachable => "Could not reach the server.",
        MeowshellErrorCode.Timeout => "The server did not respond in time.",
        MeowshellErrorCode.ConnectionLost => "The connection dropped.",
        MeowshellErrorCode.PermissionDenied => "The server denied permission.",
        MeowshellErrorCode.NotFound => "Not found on the server.",
        MeowshellErrorCode.Cancelled => "Cancelled.",
        _ => Unexplained(ex),
    };

    private static string Unexplained(TailcatException ex)
    {
        var detail = !string.IsNullOrWhiteSpace(ex.Diagnostics) ? ex.Diagnostics.Trim()
            : !string.IsNullOrWhiteSpace(ex.Message) ? ex.Message.Trim()
            : null;

        if (detail is null) return "The connection failed.";

        var lines = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var summary = string.Join(" ", lines.Take(3));
        return summary.Length > 300 ? summary[..300] + "…" : summary;
    }
}

/// <param name="WorkingDirectory">Writable directory the agent uses as its HOME.</param>
/// <param name="KnownHostsPath">Where accepted host keys are recorded.</param>
/// <param name="BinaryDirectory">Override for where the native binaries live.</param>
/// <param name="ProxyUrl">socks5:// or http:// proxy to dial through.</param>
/// <param name="ConnectTimeout">How long to wait for the connection to settle.</param>
/// <param name="AllowLegacyKeyAlgorithms">
/// Offer SHA-1 <c>ssh-rsa</c> to servers predating RFC 8332. Off by default:
/// OpenSSH itself has refused it since 8.8.
/// </param>
/// <param name="Verbose">Pass the engine's own verbose flag through.</param>
public sealed record MeowshellSshEngineOptions(
    string WorkingDirectory,
    string KnownHostsPath,
    string? BinaryDirectory = null,
    string? ProxyUrl = null,
    TimeSpan ConnectTimeout = default,
    bool AllowLegacyKeyAlgorithms = false,
    bool Verbose = false)
{
    public TimeSpan ConnectTimeout { get; init; } =
        ConnectTimeout == default ? TimeSpan.FromSeconds(30) : ConnectTimeout;
}
