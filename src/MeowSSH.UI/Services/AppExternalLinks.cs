namespace MeowSSH.UI.Services;

/// <summary>Public destinations required for a commercially distributed build.</summary>
public sealed record AppExternalLinks(Uri? PrivacyPolicy, Uri? Support, Uri? TermsOfService = null)
{
    public static AppExternalLinks Empty { get; } = new(null, null, null);
}
