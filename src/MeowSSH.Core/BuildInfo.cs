using System.Reflection;

namespace MeowSSH.Core;

/// <summary>
/// Which build this is.
/// </summary>
/// <remarks>
/// Exists because "is that fixed in the build you have?" turned out to be
/// unanswerable from a phone, and the answer changes what a symptom means: the
/// framework's error bar being visible is a layout bug in one build and a
/// genuine crash in the next. Shown on the lock and setup screens, which are
/// where someone is standing when something has gone wrong.
/// </remarks>
public static class BuildInfo
{
    /// <summary>
    /// The informational version, which CI stamps with the commit it built.
    /// </summary>
    /// <remarks>
    /// Read from the entry assembly rather than this one, so the Android app
    /// reports the app's version rather than the shared library's.
    /// </remarks>
    public static string Version { get; } = Resolve();

    private static string Resolve() => Format(
        (Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// Turns an informational version into something readable off a screen.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Resolve"/> so it can be tested: the entry
    /// assembly's real version is whatever built the test host, which is not
    /// something to assert against.
    /// </remarks>
    internal static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "unknown build";

        // The SDK appends "+<commit sha>" when the build knows its source
        // revision. The short form is what a person can compare against a log.
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return informationalVersion;

        var version = informationalVersion[..plus];
        var commit = informationalVersion[(plus + 1)..];
        return commit.Length >= 7 ? $"{version} ({commit[..7]})" : version;
    }
}
