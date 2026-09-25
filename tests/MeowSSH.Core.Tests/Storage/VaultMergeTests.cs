using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Tests.Storage;

/// <summary>
/// The merge must reach the same answer on every phone, whichever copy that
/// phone calls "local" -- so most assertions here merge both ways round and
/// compare, rather than checking one direction.
/// </summary>
public sealed class VaultMergeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid HostId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static HostRecord Host(string label, DateTimeOffset updatedAt, long revision = 1, string origin = "phone-a", DateTimeOffset? deletedAt = null) => new()
    {
        Id = HostId,
        Label = label,
        Address = "10.0.0.1",
        Username = "deploy",
        UpdatedAt = updatedAt,
        Revision = revision,
        OriginDeviceId = origin,
        DeletedAt = deletedAt,
    };

    private static VaultDocument Vault(string deviceId, long revision, params HostRecord[] hosts) => new()
    {
        DeviceId = deviceId,
        Revision = revision,
        Hosts = hosts,
    };

    private static HostRecord MergedHost(VaultDocument local, VaultDocument remote) =>
        Assert.Single(VaultMerge.Merge(local, remote, "this-phone").Merged.Hosts);

    [Fact]
    public void TheLaterEditWinsWhicheverPhoneMerges()
    {
        var mine = Vault("a", 10, Host("prod-old-name", T0));
        var theirs = Vault("b", 10, Host("prod-new-name", T0.AddMinutes(1), origin: "phone-b"));

        Assert.Equal("prod-new-name", MergedHost(mine, theirs).Label);
        Assert.Equal("prod-new-name", MergedHost(theirs, mine).Label);
    }

    [Fact]
    public void ManyOfflineEditsDoNotBeatOneLaterEdit()
    {
        // Revision counts edits; it is not a clock. Five renames made earlier
        // on one phone must lose to a single rename made afterwards elsewhere.
        var busy = Vault("a", 20, Host("renamed-five-times", T0, revision: 6));
        var later = Vault("b", 5, Host("renamed-once-later", T0.AddSeconds(30), revision: 2, origin: "phone-b"));

        Assert.Equal("renamed-once-later", MergedHost(busy, later).Label);
        Assert.Equal("renamed-once-later", MergedHost(later, busy).Label);
    }

    [Fact]
    public void EditsInTheSameInstantAreBrokenByRevisionThenDeviceTheSameWayOnBothPhones()
    {
        var lowerRevision = Vault("a", 3, Host("rev-1", T0, revision: 1, origin: "phone-z"));
        var higherRevision = Vault("b", 3, Host("rev-2", T0, revision: 2, origin: "phone-a"));
        Assert.Equal("rev-2", MergedHost(lowerRevision, higherRevision).Label);
        Assert.Equal("rev-2", MergedHost(higherRevision, lowerRevision).Label);

        var fromA = Vault("a", 3, Host("from-a", T0, revision: 1, origin: "phone-a"));
        var fromB = Vault("b", 3, Host("from-b", T0, revision: 1, origin: "phone-b"));
        Assert.Equal("from-b", MergedHost(fromA, fromB).Label);
        Assert.Equal("from-b", MergedHost(fromB, fromA).Label);
    }

    [Fact]
    public void ADeletionMadeLaterRemovesTheHostEverywhere()
    {
        var kept = Vault("a", 4, Host("to-delete", T0));
        var deleted = Vault("b", 4, Host("to-delete", T0.AddMinutes(1), revision: 2, origin: "phone-b", deletedAt: T0.AddMinutes(1)));

        Assert.True(MergedHost(kept, deleted).IsDeleted);
        Assert.True(MergedHost(deleted, kept).IsDeleted);
    }

    [Fact]
    public void AnEditMadeAfterAnotherPhoneDeletedTheHostBringsItBack()
    {
        // Last action wins, deletion included: whoever touched it last meant it.
        var deleted = Vault("a", 4, Host("h", T0, revision: 2, deletedAt: T0));
        var edited = Vault("b", 4, Host("h-edited", T0.AddMinutes(1), revision: 2, origin: "phone-b"));

        Assert.False(MergedHost(deleted, edited).IsDeleted);
        Assert.False(MergedHost(edited, deleted).IsDeleted);
    }

    [Fact]
    public void RecordsOnlyOnePhoneHasAreKeptFromEitherSide()
    {
        var onlyMine = Host("mine", T0) with { Id = Guid.NewGuid() };
        var onlyTheirs = Host("theirs", T0) with { Id = Guid.NewGuid() };
        var credential = new CredentialRecord
        {
            Id = Guid.NewGuid(), Label = "deploy key", Kind = CredentialKind.Password, Secret = [1, 2, 3],
            UpdatedAt = T0, Revision = 1, OriginDeviceId = "phone-b",
        };

        var (merged, changed) = VaultMerge.Merge(
            Vault("a", 1, onlyMine),
            Vault("b", 1, onlyTheirs) with { Credentials = [credential] },
            "a");

        Assert.True(changed);
        Assert.Equal(["mine", "theirs"], merged.Hosts.Select(h => h.Label));
        Assert.Equal(credential.Id, Assert.Single(merged.Credentials).Id);
    }

    [Fact]
    public void KeysStayThisPhonesOwnAndTheRevisionIsNeverBehindEitherCopy()
    {
        var myKey = new WrappedVaultKey("device", KeyWrapMethod.DeviceKeyStore, [1], null, null);
        var theirKey = new WrappedVaultKey("device", KeyWrapMethod.DeviceKeyStore, [2], null, null);

        var (merged, _) = VaultMerge.Merge(
            Vault("a", 7) with { WrappedKeys = [myKey] },
            Vault("b", 42) with { WrappedKeys = [theirKey] },
            "a");

        Assert.Same(myKey, Assert.Single(merged.WrappedKeys));
        Assert.Equal(42, merged.Revision);
    }

    [Fact]
    public void NothingNewFromTheOtherPhoneIsReportedAsNoChange()
    {
        var current = Host("same", T0.AddMinutes(5), revision: 3);
        var older = Host("stale", T0, revision: 1);

        var (_, changed) = VaultMerge.Merge(Vault("a", 9, current), Vault("b", 2, older), "a");

        Assert.False(changed);
    }

    [Fact]
    public void AVaultRestoredFromAnotherPhoneAdoptsThisInstallsId()
    {
        // Restoring copies the source phone's id; without adopting its own, this
        // phone's later edits would be attributed to a phone that did not make them.
        var (merged, changed) = VaultMerge.Merge(Vault("source-phone", 1), Vault("source-phone", 1), "this-phone");

        Assert.True(changed);
        Assert.Equal("this-phone", merged.DeviceId);
    }
}
