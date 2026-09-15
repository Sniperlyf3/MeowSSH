namespace MeowSSH.UI.Services;

public interface IQrScanner
{
    Task<string?> ScanAsync(CancellationToken cancellationToken = default);
}
