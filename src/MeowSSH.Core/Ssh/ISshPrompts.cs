using MeowSSH.Core.Security;

namespace MeowSSH.Core.Ssh;

/// <summary>
/// Everything the handshake may need to ask a person mid-connection.
/// </summary>
/// <remarks>
/// These arrive while the SSH handshake is paused waiting for an answer, so each
/// one blocks a connection attempt until it returns. Implementations must show UI
/// and await a real decision rather than guessing a default — silently accepting
/// an unknown host key would defeat the point of asking.
/// </remarks>
public interface ISshPrompts
{
    /// <summary>
    /// Asks whether to trust a host being seen for the first time.
    /// </summary>
    /// <returns>True to accept and remember the key.</returns>
    Task<bool> ConfirmUnknownHostKeyAsync(HostKeyPrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>Asks for a password.</summary>
    /// <returns>The password, or null if the user cancelled.</returns>
    Task<SecretBuffer?> RequestPasswordAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>Asks for the passphrase protecting a private key.</summary>
    Task<SecretBuffer?> RequestKeyPassphraseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Presents a keyboard-interactive challenge — how a server asks for a
    /// one-time code, or anything else its PAM stack wants.
    /// </summary>
    /// <returns>One answer per question, or null if the user cancelled.</returns>
    Task<IReadOnlyList<string>?> AnswerChallengeAsync(KeyboardInteractivePrompt prompt, CancellationToken cancellationToken = default);
}

/// <param name="Host">The host as the user asked for it.</param>
/// <param name="Fingerprint">SHA256 fingerprint, in the form ssh-keygen prints.</param>
/// <param name="KeyType">e.g. <c>ssh-ed25519</c>.</param>
public sealed record HostKeyPrompt(string Host, string Fingerprint, string KeyType);

/// <param name="Name">The server's name for this challenge, often empty.</param>
/// <param name="Instruction">Text the server wants shown above the questions.</param>
/// <param name="Questions">One prompt per answer required.</param>
/// <param name="Echo">
/// Per question, whether the typed answer may be shown. False for a password,
/// true for something like a one-time code the user is reading off a device.
/// </param>
public sealed record KeyboardInteractivePrompt(
    string Name,
    string Instruction,
    IReadOnlyList<string> Questions,
    IReadOnlyList<bool> Echo);
