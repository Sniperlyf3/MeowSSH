namespace MeowSSH.Core.Ssh;

/// <summary>Why a connection failed, in the terms the UI has to branch on.</summary>
/// <remarks>
/// Typed rather than a message string because the app reacts differently to each:
/// a changed host key is a full-stop warning screen, a failed password is a retry,
/// an unreachable network is a "try again later". Matching on error text would
/// break the first time a message was reworded.
/// </remarks>
public enum SshFailure
{
    Unknown,
    AuthenticationFailed,

    /// <summary>The host is not in known_hosts and the user has not yet decided.</summary>
    HostKeyUnknown,

    /// <summary>
    /// The host presented a different key than the one recorded. Either the server
    /// was rebuilt, or someone is between you and it — and the app cannot tell
    /// which, so the user must.
    /// </summary>
    HostKeyChanged,

    NetworkUnreachable,
    Timeout,
    ConnectionLost,
    PermissionDenied,
    NotFound,
    Cancelled,
}

public sealed class SshException(SshFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public SshFailure Failure { get; } = failure;

    /// <summary>
    /// Whether retrying the same way could plausibly work. A changed host key
    /// never retries: it needs a decision, not another attempt.
    /// </summary>
    public bool IsRetryable => Failure is SshFailure.NetworkUnreachable
        or SshFailure.Timeout
        or SshFailure.ConnectionLost;
}
