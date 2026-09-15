using System.Text.Json;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class TailcatIdentityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"meowssh-tailcat-{Guid.NewGuid():N}");

    [Fact]
    public async Task ImportThenExportRoundTripsPrivateIdentityJson()
    {
        var store = CreateStore();
        const string json = "{\"Private\":\"privkey:test\",\"Public\":\"nodekey:test\"}";

        await store.ImportAsync("phone", json);
        var exported = await store.ExportAsync("phone");

        using var expected = JsonDocument.Parse(json);
        using var actual = JsonDocument.Parse(exported);
        Assert.Equal(expected.RootElement.GetProperty("Private").GetString(), actual.RootElement.GetProperty("Private").GetString());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public async Task ImportRejectsPathTraversalNames(string name)
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.ImportAsync(name, "{\"Private\":\"privkey:test\"}"));
    }

    [Fact]
    public async Task ImportDoesNotOverwriteUnlessExplicitlyRequested()
    {
        var store = CreateStore();
        await store.ImportAsync("phone", "{\"Private\":\"one\"}");

        await Assert.ThrowsAsync<IOException>(() => store.ImportAsync("phone", "{\"Private\":\"two\"}"));

        await store.ImportAsync("phone", "{\"Private\":\"two\"}", overwrite: true);
        Assert.Contains("two", await store.ExportAsync("phone"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportRejectsJsonWithoutPrivateKeyMaterial()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.ImportAsync("bad", "{\"Public\":\"nodekey:test\"}"));
    }

    private TailcatIdentityStore CreateStore() => new(new TailcatHubRuntimeOptions(
        BinaryDirectory: _root,
        HomeDirectory: Path.Combine(_root, "home"),
        WorkDirectory: Path.Combine(_root, "work")));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
