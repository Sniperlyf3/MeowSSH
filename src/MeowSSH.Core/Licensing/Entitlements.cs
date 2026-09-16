namespace MeowSSH.Core.Licensing;

public enum EntitlementTier
{
    Free = 0,
    Pro = 1,
    ProCloud = 2,
    Team = 3,
}

public enum PremiumFeature
{
    TailcatWorkspaces,
    TailcatPhoneServer,
    TailcatExitNode,
    TailcatFullDeviceVpn,
    TailcatAdvancedRouting,
    AdvancedSftp,
    MultiHostActions,
    CommandMonitoring,
    HostHealthDashboard,
    SessionLogs,
    PremiumCustomization,
    EncryptedLocalBackup,
    CloudBackup,
    CloudSync,
    PushMonitoring,
    HostedAi,
    TeamSharing,
    TeamAudit,
}

public enum EntitlementSource
{
    None,
    CachedVerifiedGrant,
    GooglePlay,
    ServerVerifiedGooglePlay,
    Promotional,
    Team,
}

public sealed record EntitlementSnapshot(
    EntitlementTier Tier,
    EntitlementSource Source,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? ValidUntilUtc = null,
    string? GrantId = null)
{
    public static EntitlementSnapshot Free(DateTimeOffset now) =>
        new(EntitlementTier.Free, EntitlementSource.None, now);

    public bool IsExpired(DateTimeOffset now) =>
        ValidUntilUtc is { } until && until <= now;
}

public static class EntitlementPolicy
{
    public static bool Allows(EntitlementSnapshot entitlement, PremiumFeature feature, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        if (entitlement.IsExpired(now)) return false;

        var requiredTier = RequiredTier(feature);
        return entitlement.Tier >= requiredTier;
    }

    public static EntitlementTier RequiredTier(PremiumFeature feature) => feature switch
    {
        PremiumFeature.CloudBackup or
        PremiumFeature.CloudSync or
        PremiumFeature.PushMonitoring or
        PremiumFeature.HostedAi => EntitlementTier.ProCloud,

        PremiumFeature.TeamSharing or
        PremiumFeature.TeamAudit => EntitlementTier.Team,

        _ => EntitlementTier.Pro,
    };
}

public static class MeowSshProducts
{
    // Create these identifiers verbatim in Play Console before launch.
    // Pro Cloud is one subscription product with two base plans, matching
    // Google Play's current subscription model.
    public const string ProLifetime = "meowssh_pro_lifetime";
    public const string ProCloud = "meowssh_pro_cloud";
    public const string ProCloudMonthlyBasePlan = "monthly";
    public const string ProCloudYearlyBasePlan = "yearly";
}
