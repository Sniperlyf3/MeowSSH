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
            // Init-only, so it has to be set here rather than conditionally
            // afterwards; null means "find the packaged binaries".
            BinaryDirectory = options.BinaryDirectory!,
        };

        MeowshellAgentConnection? agent = null;
        try
        {
            agent = await MeowshellAgentConnection.ConnectAsync(
                clientOptions,
                destination: Destination(host),
                configure: Configure(credentials),
                port: host.Transport == SshTransport.Tailcat
                    ? null
                    : host.Port.ToString(CultureInfo.InvariantCulture),
                jumpHosts: null,
                knownHostsPath: options.KnownHostsPath,
                proxyUrl: options.ProxyUrl,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Subscribed after the connection exists, because that is the only
            // point at which there is an instance to subscribe to. See the note
            // on ISshPrompts about what this cannot cover.
            Attach(agent, prompts);
            return new MeowshellSshConnection(host.Id, agent);
        }
        catch (TailcatException ex)
        {
            if (agent is not null) await agent.DisposeAsync().ConfigureAwait(false);
            throw Translate(ex);
        }
        catch (FileNotFoundException ex)
        {
            // Its own case, and ahead of IOException because it derives from it:
            // a missing native binary reported as a storage problem sends anyone
            // reading it to look in entirely the wrong place. That is exactly
            // what happened on the first device this ran on.
            if (agent is not null) await agent.DisposeAsync().ConfigureAwait(false);
            throw new SshException(
                SshFailure.Unknown,
                "MeowSSH could not find the SSH helper binaries it ships with. The app may be packaged incorrectly.",
                ex);
        }
        catch (IOException ex)
        {
            // Meowshell validates the agent's HOME before it starts anything and
            // reports a refusal as an IOException, which is outside the typed
            // error model the rest of this method translates. Left to escape it
            // would surface as an unhandled exception on the connect path rather
            // than as something the UI can explain. The message does not name a
            // cause, because this catch covers more than one.
            if (agent is not null) await agent.DisposeAsync().ConfigureAwait(false);
            throw new SshException(
                SshFailure.Unknown,
                $"MeowSSH could not set up local storage for the connection: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Creates the agent's HOME so that only this user can reach it.
    /// </summary>
    /// <remarks>
    /// The mode is set explicitly rather than left to the process umask. This
    /// directory becomes the agent's HOME, which is where known_hosts and any
    /// session key material live, and Meowshell refuses to launch against a HOME
    /// that grants group or other access -- correctly, since another local user
    /// able to write there could redirect host-key verification. Under the usual
    /// umask of 022 the default would be 0755 and every connection would fail.
    /// </remarks>
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

        // An existing directory from an older build was created with the umask
        // applied, so it may be 0755. Narrowing it is safe and is what the user
        // would want; leaving it would make every connection fail from here on.
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode sharedAccess =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & sharedAccess) != 0) File.SetUnixFileMode(path, mode & ~sharedAccess);
    }

    private MeowshellAgentConfigureOptions Configure(SshCredentials credentials) => new()
    {
        // There is no ssh-agent on Android, and on a desktop the user's agent is
        // not this app's to spend: keys come from the vault.
        DisableLocalAgent = true,
        AllowLegacyKeyAlgorithms = options.AllowLegacyKeyAlgorithms,
        // Copied out of their pinned buffers only here, at the call that needs
        // them; the caller disposes the originals once the handshake is done.
        PrivateKeys = [.. credentials.PrivateKeys.Select(k => k.ReadOnlySpan.ToArray())],
        Certificates = [.. credentials.Certificates],
        KeystoreKeyIds = [.. credentials.KeyStoreKeyIds],
        KeystorePublicKeys = [.. credentials.KeyStorePublicKeys],
    };

    private static string Destination(HostRecord host) => host.Transport switch
    {
        SshTransport.Tailcat => host.Address,
        _ => host.Username is null ? host.Address : $"{host.Username}@{host.Address}",
    };

    private static void Attach(MeowshellAgentConnection agent, ISshPrompts prompts)
    {
        agent.HostKeyPromptRequested += async (prompt, ct) =>
            await prompts.ConfirmUnknownHostKeyAsync(
                new HostKeyPrompt(prompt.Remote, prompt.Fingerprint, KeyType: ""), ct).ConfigureAwait(false);

        agent.PasswordRequested += async (prompt, ct) =>
        {
            using var secret = await prompts.RequestPasswordAsync(prompt, ct).ConfigureAwait(false);
            return SecretToString(secret);
        };

        agent.PassphraseRequested += async ct =>
        {
            using var secret = await prompts.RequestKeyPassphraseAsync(ct).ConfigureAwait(false);
            return SecretToString(secret);
        };

        agent.KeyboardInteractiveRequested += async (prompt, ct) =>
        {
            var answers = await prompts.AnswerChallengeAsync(
                new KeyboardInteractivePrompt(prompt.Name, prompt.Instruction, prompt.Questions, prompt.Echos),
                ct).ConfigureAwait(false);
            return answers is null ? [] : [.. answers];
        };
    }

    /// <summary>
    /// Converts a secret to the string the agent protocol requires, at the last
    /// possible moment.
    /// </summary>
    /// <remarks>
    /// This is where the buffer discipline ends and cannot be helped: the protocol
    /// carries the answer as JSON, so it has to become a string, and a .NET string
    /// cannot be overwritten afterwards. Keeping the conversion here means it
    /// happens exactly once, at the boundary, rather than throughout the app.
    /// </remarks>
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

    // Written for a person. The engine's own diagnostics are useful in a log and
    // alarming in a dialog, so they do not go in the message.
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

        // The one case with nothing better to say, and so the one case where
        // the engine's own words have to be passed through. Replacing them with
        // "The connection failed." leaves the user with nothing to act on and
        // whoever is helping them with nothing to go on -- which is worse than a
        // sentence written for a different audience.
        _ => Unexplained(ex),
    };

    /// <summary>
    /// Everything known about a failure MeowSSH has no words of its own for.
    /// </summary>
    /// <remarks>
    /// The agent's stderr is the useful part and is usually the only part: a
    /// failure with no typed reason is one this app did not anticipate, and the
    /// process that did the work is the only thing that knows what happened.
    /// </remarks>
    private static string Unexplained(TailcatException ex)
    {
        var detail = !string.IsNullOrWhiteSpace(ex.Diagnostics) ? ex.Diagnostics.Trim()
            : !string.IsNullOrWhiteSpace(ex.Message) ? ex.Message.Trim()
            : null;

        if (detail is null) return "The connection failed.";

        // Long stderr is a wall of text in a dialog. The first lines carry the
        // cause; the rest is usually a stack of context nobody reads on a phone.
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
