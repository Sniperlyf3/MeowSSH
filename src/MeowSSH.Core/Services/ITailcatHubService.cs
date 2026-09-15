namespace MeowSSH.Core.Services;

/// <summary>One place for the app's long-lived Tailcat server/client features.</summary>
public interface ITailcatHubService : IAsyncDisposable
{
    TailcatHubSnapshot Snapshot { get; }
    event EventHandler? Changed;

    Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default);
    Task StopServerAsync(CancellationToken cancellationToken = default);

    Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default);
    Task StopSocksAsync(CancellationToken cancellationToken = default);

    Task<TailcatForwardSnapshot> StartForwardAsync(TailcatForwardRequest request, CancellationToken cancellationToken = default);
    Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default);
    Task<TailcatGeneratedKey> GenerateKeyAsync(TailcatGenerateKeyRequest request, CancellationToken cancellationToken = default);
    Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default);
    Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default);

    Task<string> ResolveAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default);
    Task<TailcatAddressDetails> InspectAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default);
    Task<TailcatDiagnosticResult> DiagnoseAsync(string address, bool waitForDirect, string? derpMapUrl = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(string address, string path, string? derpMapUrl = null, CancellationToken cancellationToken = default);
    Task UploadAsync(string localPath, string address, string remotePath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default);
    Task DownloadAsync(string address, string remotePath, string localPath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default);
}

public sealed record TailcatHubSnapshot(
    TailcatServerSnapshot? Server,
    TailcatSocksSnapshot? Socks,
    IReadOnlyList<TailcatForwardSnapshot> Forwards,
    IReadOnlyList<string> RecentLogLines);

public sealed record TailcatServerSnapshot(
    string Address,
    DateTimeOffset ExpiresAt,
    bool ShellEnabled,
    bool FilesEnabled,
    bool ExitNodeEnabled,
    bool AllowsAnyClient,
    bool UseTailcatCredentialForShell,
    string? SharedFolder,
    string FileMode);

public sealed record TailcatSocksSnapshot(string ListenAddress, string? ClientKey);

public sealed record TailcatForwardSnapshot(
    Guid Id,
    string Address,
    IReadOnlyList<string> Mappings,
    IReadOnlyList<string> BoundAddresses,
    string? ClientKey);

public sealed record TailcatServeRequest(
    TimeSpan Lifetime,
    string AllowedClientKeys,
    bool EnableShell,
    string? AuthorizedSshKeys,
    bool EnableFiles,
    string? SharedFolder,
    string FileMode,
    bool EnableExitNode,
    bool AllowAnyClient = false,
    bool UseTailcatCredentialForShell = false,
    bool FullAddress = false,
    bool UsePresharedKey = true,
    string? DerpMapUrl = null,
    string? PrivateKeyJson = null);

public sealed record TailcatSocksRequest(
    string ListenAddress = "127.0.0.1:0",
    string? ClientKey = null,
    string? DerpMapUrl = null);

public sealed record TailcatForwardRequest(
    string Address,
    IReadOnlyList<string> Mappings,
    string BindAddress = "127.0.0.1",
    string? ClientKey = null,
    string? DerpMapUrl = null);

public sealed record TailcatGenerateKeyRequest(
    string Name,
    bool Client,
    string? Region = null,
    bool FixedRegion = false,
    bool EmbedDerpMap = false,
    bool UsePresharedKey = true,
    bool Overwrite = false,
    string? DerpMapUrl = null);

public sealed record TailcatGeneratedKey(string Name, bool Client, string Value);

public sealed record TailcatAddressDetails(
    string ResolvedAddress,
    string ServerPublicKey,
    string? ServerDiscoPublicKey,
    bool HasPresharedKey,
    long RegionId,
    IReadOnlyList<TailcatDerpRegionDetails> EmbeddedRegions);

public sealed record TailcatDerpRegionDetails(
    long RegionId,
    string? Code,
    string? Name,
    IReadOnlyList<TailcatDerpNodeDetails> Nodes);

public sealed record TailcatDerpNodeDetails(
    string? Name,
    string? HostName,
    string? CertName,
    string? IPv4,
    string? IPv6,
    int StunPort,
    int DerpPort);

public sealed record TailcatDiagnosticResult(
    string ResolvedAddress,
    bool PingSucceeded,
    TimeSpan? Latency,
    bool? Direct,
    string? Via,
    long RegionId,
    string? RegionName,
    string ServerPublicKey,
    bool HasPresharedKey);

public sealed record TailcatRemoteFile(
    string Name,
    bool IsDirectory,
    string? Mode,
    long? Size,
    DateTime? ModifiedAt);
