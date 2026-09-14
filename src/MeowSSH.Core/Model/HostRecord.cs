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
    Tcp,
    Tailcat,
    TailscaleSsh,
}

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space,
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two,
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
    public required string Label { get; init; }

    /// <summary>
    /// Hostname, IP, tailcat address, or serial-device identifier depending on
    /// <see cref="Protocol"/> and <see cref="Transport"/>. Local terminals leave it empty.
    /// </summary>
    public required string Address { get; init; }

    public int Port { get; init; } = 22;
    public string? Username { get; init; }
    public HostProtocol Protocol { get; init; } = HostProtocol.Ssh;
    public SshTransport Transport { get; init; } = SshTransport.Tcp;

    /// <summary>Automatically reconnect after an unexpected network/session loss.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>USB serial line settings. Ignored unless <see cref="Protocol"/> is Serial.</summary>
    public int SerialBaudRate { get; init; } = 115200;
    public int SerialDataBits { get; init; } = 8;
    public SerialStopBits SerialStopBits { get; init; } = SerialStopBits.One;
    public SerialParity SerialParity { get; init; } = SerialParity.None;

    public IReadOnlyList<string> Tags { get; init; } = [];
    public Guid? JumpHostId { get; init; }
    public Guid? CredentialId { get; init; }
    public DateTimeOffset? LastConnectedAt { get; init; }

    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? OriginDeviceId { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }

    public bool IsDeleted => DeletedAt is not null;

    public string DisplayAddress => Protocol switch
    {
        HostProtocol.Local => "Local terminal",
        HostProtocol.Serial => string.IsNullOrWhiteSpace(Address)
            ? $"USB serial · {SerialBaudRate} baud"
            : $"{Address} · {SerialBaudRate} baud",
        HostProtocol.Telnet => Address + (Port == 23 ? "" : $":{Port}"),
        HostProtocol.Mosh => (Username is null ? Address : $"{Username}@{Address}") + (Port == 22 ? "" : $":{Port}"),
        _ => Transport switch
        {
            SshTransport.Tailcat => Address.Length > 22 ? string.Concat(Address.AsSpan(0, 20), "…") : Address,
            _ => (Username is null ? Address : $"{Username}@{Address}") + (Port == 22 ? "" : $":{Port}"),
        },
    };

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
