namespace MeowSSH.App.Services;

internal static class LicensingBuildConfig
{
    // These values are public configuration, not secrets. The private signing key
    // lives only on the licensing service. Empty values intentionally fail closed.
    public const string ApiBaseUrl = "";
    public const string PublicKeySubjectPublicKeyInfoBase64 = "";
}
