using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Tests.Licensing;

public class EntitlementTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreeDoesNotUnlockPaidFeatures()
    {
        var entitlement = EntitlementSnapshot.Free(Now);
        Assert.False(EntitlementPolicy.Allows(entitlement, PremiumFeature.TailcatWorkspaces, Now));
    }

    [Fact]
    public void ProUnlocksLocalPremiumFeaturesButNotCloud()
    {
        var entitlement = new EntitlementSnapshot(EntitlementTier.Pro, EntitlementSource.ServerVerifiedGooglePlay, Now);

        Assert.True(EntitlementPolicy.Allows(entitlement, PremiumFeature.TailcatFullDeviceVpn, Now));
        Assert.True(EntitlementPolicy.Allows(entitlement, PremiumFeature.MultiHostActions, Now));
        Assert.False(EntitlementPolicy.Allows(entitlement, PremiumFeature.CloudSync, Now));
    }

    [Fact]
    public void ProCloudUnlocksCloudButNotTeamFeatures()
    {
        var entitlement = new EntitlementSnapshot(EntitlementTier.ProCloud, EntitlementSource.ServerVerifiedGooglePlay, Now);

        Assert.True(EntitlementPolicy.Allows(entitlement, PremiumFeature.CloudSync, Now));
        Assert.False(EntitlementPolicy.Allows(entitlement, PremiumFeature.TeamAudit, Now));
    }

    [Fact]
    public void ExpiredGrantDoesNotUnlockFeatures()
    {
        var entitlement = new EntitlementSnapshot(
            EntitlementTier.Team,
            EntitlementSource.Team,
            Now.AddDays(-2),
            Now.AddSeconds(-1));

        Assert.False(EntitlementPolicy.Allows(entitlement, PremiumFeature.TeamSharing, Now));
        Assert.False(EntitlementPolicy.Allows(entitlement, PremiumFeature.TailcatWorkspaces, Now));
    }
}
