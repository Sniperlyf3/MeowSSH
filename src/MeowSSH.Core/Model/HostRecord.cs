namespace MeowSSH.Core.Model;

/// <summary>How MeowSSH reaches a host.</summary>
public enum SshTransport
{
    /// <summary>An ordinary SSH server reached over TCP.</summary>
    Tcp,

    /// <summary>
    /// A tailcat address: Tailscale's data plane with no control plane, where the
    /// address itself is the credential and no port needs to be open anywhere.
    /// </summary>
    Tailcat,

    /// <summary>
    /// A Tailscale SSH host, reached over TCP across the tailnet. Requires the
    /// Tailscale app to be connected on this device; identity comes from the
    /// tailnet rather than from an SSH key.
    /// </summary>
    TailscaleSsh,
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

/// <summary>
/// A saved host.
/// </summary>
/// <remarks>
/// <para>
/// Every field that sync will eventually need is already here, because adding
/// them later would mean migrating a database whose rows are encrypted and whose
/// only copy may be on a device that is offline. <see cref="Revision"/> and
/// <see cref="UpdatedAt"/> let two devices decide which edit won;
/// <see cref="OriginDeviceId"/> breaks ties between edits made in the same
/// instant; <see cref="DeletedAt"/> is a tombstone, because a row that simply
/// vanished is indistinguishable from one that never synced.
/// </para>
/// <para>
/// Nothing here is secret. Credentials live in their own records, sealed
/// separately, so listing hosts never requires unsealing a password.
/// </para>
/// </remarks>
public sealed record HostRecord
{
    public required Guid Id { get; init; }

    /// <summary>What the user calls this host.</summary>
    public required string Label { get; init; }

    /// <summary>Hostname, IP, or tailcat address, depending on <see cref="Transport"/>.</summary>
    public required string Address { get; init; }

    public int Port { get; init; } = 22;

    public string? Username { get; init; }

    public SshTransport Transport { get; init; } = SshTransport.Tcp;

    /// <summary>Free-form labels the user groups hosts by.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Id of the host this one is reached through, if it sits behind a bastion.</summary>
    public Guid? JumpHostId { get; init; }

    /// <summary>
    /// The credential this host signs in with, if one has been chosen.
    /// </summary>
    /// <remarks>
    /// A reference rather than the secret itself: one deploy key across a fleet is
    /// the normal case, and copying it into every host would mean rotating it in
    /// as many places as there are servers.
    /// </remarks>
    public Guid? CredentialId { get; init; }

    public DateTimeOffset? LastConnectedAt { get; init; }

    // Sync bookkeeping ----------------------------------------------------

    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? OriginDeviceId { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>
    /// How this host is written in the places SSH itself writes it, e.g.
    /// <c>deploy@build-01:2222</c>.
    /// </summary>
    public string DisplayAddress => Transport switch
    {
        SshTransport.Tailcat => Address.Length > 22 ? string.Concat(Address.AsSpan(0, 20), "…") : Address,
        _ => (Username is null ? Address : $"{Username}@{Address}") + (Port == 22 ? "" : $":{Port}"),
    };

    /// <summary>Two letters for the host's avatar, taken from its label.</summary>
    public string Initials
    {
        get
        {
            var words = Label.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
            return words.Length switch
            {
                0 => "?",
                1 => words[0].Length == 1 ? words[0] : words[0][..2],
                _ => $"{words[0][0]}{words[1][0]}",
            };
        }
    }
}
