namespace MeowSSH.Core.Model;

/// <summary>The terminal protocol used for a saved connection.</summary>
public enum HostProtocol
{
    Ssh,
    Mosh,
    Telnet,
    Serial,
    Local,
}

/// <summary>How MeowSSH reaches an SSH or Mosh host.</summary>
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

/// <summary>A saved host or terminal endpoint.</summary>
public sealed record HostRecord
{
    public required Guid Id { get; init; }

    /// <summary>What the user calls this endpoint.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Hostname, IP, tailcat address, or serial-device identifier depending on
    /// <see cref="Protocol"/> and <see cref="Transport"/>. Local terminals leave it empty.
    /// </summary>
    public required string Address { get; init; }

    public int Port { get; init; } = 22;

    public string? Username { get; init; }

    /// <summary>The terminal protocol. Older vaults migrate to SSH.</summary>
    public HostProtocol Protocol { get; init; } = HostProtocol.Ssh;

    public SshTransport Transport { get; init; } = SshTransport.Tcp;

    /// <summary>Automatically reconnect after an unexpected network/session loss.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>Free-form labels the user groups hosts by.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Id of the host this one is reached through, if it sits behind a bastion.</summary>
    public Guid? JumpHostId { get; init; }

    /// <summary>The credential this host signs in with, if one has been chosen.</summary>
    public Guid? CredentialId { get; init; }

    public DateTimeOffset? LastConnectedAt { get; init; }

    // Sync bookkeeping ----------------------------------------------------

    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? OriginDeviceId { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>Human-readable endpoint shown in lists and session headers.</summary>
    public string DisplayAddress => Protocol switch
    {
        HostProtocol.Local => "Local terminal",
        HostProtocol.Serial => string.IsNullOrWhiteSpace(Address) ? "USB serial" : Address,
        HostProtocol.Telnet => Address + (Port == 23 ? "" : $":{Port}"),
        HostProtocol.Mosh => (Username is null ? Address : $"{Username}@{Address}") + (Port == 22 ? "" : $":{Port}"),
        _ => Transport switch
        {
            SshTransport.Tailcat => Address.Length > 22 ? string.Concat(Address.AsSpan(0, 20), "…") : Address,
            _ => (Username is null ? Address : $"{Username}@{Address}") + (Port == 22 ? "" : $":{Port}"),
        },
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
