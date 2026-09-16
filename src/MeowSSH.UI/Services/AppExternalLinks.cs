namespace MeowSSH.UI.Services;

/// <summary>Public destinations required for a commercially distributed build.</summary>
public sealed record AppExternalLinks(Uri? PrivacyPolicy, Uri? Support)
{
    public static AppExternalLinks Empty { get; } = new(null, null);
}
