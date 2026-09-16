namespace MeowSSH.UI.Tests;

public class DeviceKeyCopyTests
{
    [Fact]
    public void MissingDeviceKeyCopyDoesNotPromisePrivateKeyRestore()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/MeowSSH.UI/Pages/KeysPage.razor"));
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("this non-exportable private key cannot be restored", source, StringComparison.Ordinal);
        Assert.Contains("Generate a replacement key and update the server", source, StringComparison.Ordinal);
        Assert.DoesNotContain("restore or generate a replacement key", source, StringComparison.OrdinalIgnoreCase);
    }
}
