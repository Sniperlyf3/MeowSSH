using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class FileTailcatTemporaryShareStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meowssh-temp-shares-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var temporary = _path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
    }

    [Fact]
    public void EmptyOrWhitespacePathIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new FileTailcatTemporaryShareStore(""));
        Assert.Throws<ArgumentException>(() => new FileTailcatTemporaryShareStore("   "));
    }

    [Fact]
    public async Task GetAllOnAMissingFileReturnsEmptyRatherThanThrowing()
    {
        var store = new FileTailcatTemporaryShareStore(_path);

        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task SavedSharesSurviveAFreshStoreInstanceOverTheSameFile()
    {
        var share = Share("First");
        await new FileTailcatTemporaryShareStore(_path).SaveAsync(share);

        // A brand-new instance, not the one that wrote it -- proves this is
        // durable persistence, not an in-memory cache that merely resembles one.
        var reloaded = await new FileTailcatTemporaryShareStore(_path).GetAllAsync();

        // Record equality on TailcatTemporaryShare would compare ServeTargets by
        // reference-sensitive list-type equality, which round-tripping through
        // JSON never preserves -- assert the fields that matter instead.
        var reread = Assert.Single(reloaded);
        Assert.Equal(share.Id, reread.Id);
        Assert.Equal(share.Label, reread.Label);
        Assert.Equal(share.ServeTargets, reread.ServeTargets);
        Assert.Equal(share.AllowedClientKeys, reread.AllowedClientKeys);
        Assert.Equal(share.Address, reread.Address);
        Assert.Equal(share.CreatedAtUtc, reread.CreatedAtUtc);
        Assert.Equal(share.ExpiresAtUtc, reread.ExpiresAtUtc);
        Assert.Null(reread.EndedAtUtc);
        Assert.Null(reread.EndReason);
    }

    [Fact]
    public async Task SavingWithAnExistingIdOverwritesRatherThanDuplicates()
    {
        var store = new FileTailcatTemporaryShareStore(_path);
        var share = Share("Original");
        await store.SaveAsync(share);

        var revoked = share with { EndedAtUtc = DateTimeOffset.UtcNow, EndReason = TailcatTemporaryShareEndReason.Revoked };
        await store.SaveAsync(revoked);

        var all = await store.GetAllAsync();
        var only = Assert.Single(all);
        Assert.Equal(TailcatTemporaryShareEndReason.Revoked, only.EndReason);
    }

    [Fact]
    public async Task GetAllOrdersMostRecentlyCreatedFirst()
    {
        var store = new FileTailcatTemporaryShareStore(_path);
        var older = Share("Older", createdAtUtc: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = Share("Newer", createdAtUtc: DateTimeOffset.UtcNow);
        await store.SaveAsync(older);
        await store.SaveAsync(newer);

        var all = await store.GetAllAsync();

        Assert.Equal(newer.Id, all[0].Id);
        Assert.Equal(older.Id, all[1].Id);
    }

    [Fact]
    public async Task HistoryIsBoundedToFiftyEntries()
    {
        var store = new FileTailcatTemporaryShareStore(_path);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 51; i++)
        {
            await store.SaveAsync(Share($"Share {i}", createdAtUtc: now.AddMinutes(i)));
        }

        var all = await store.GetAllAsync();

        Assert.Equal(50, all.Count);
        // The oldest one (index 0, i.e. "Share 0") must be the one dropped --
        // otherwise this "cap" could silently be discarding the newest entry
        // instead, which would hide an in-progress share from the UI.
        Assert.DoesNotContain(all, item => item.Label == "Share 0");
        Assert.Contains(all, item => item.Label == "Share 50");
    }

    private static TailcatTemporaryShare Share(string label, DateTimeOffset? createdAtUtc = null) => new(
        Guid.NewGuid(),
        label,
        ["8080"],
        "nodekey:test-client",
        "tc-test-address",
        createdAtUtc ?? DateTimeOffset.UtcNow,
        (createdAtUtc ?? DateTimeOffset.UtcNow) + TimeSpan.FromMinutes(30),
        EndedAtUtc: null,
        EndReason: null);
}
