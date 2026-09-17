using MeowSSH.Core.Model;

namespace MeowSSH.Core.Services;

/// <summary>How a saved connection relates to DERP relay infrastructure.</summary>
public enum RelayClass
{
    NotApplicable,
    PublicDefault,
    MeowSsh,
    SelfHosted,
    Mixed,
}

/// <summary>
/// Classifies a parsed Tailcat address without performing I/O. The caller owns
/// the MeowSSH relay allowlist so relay hostnames remain build/deployment data,
/// not constants embedded in this policy primitive.
/// </summary>
public static class TailcatRelayClassifier
{
    public static RelayClass Classify(
        SshTransport transport,
        TailcatAddressDetails details,
        IEnumerable<string> meowSshRelayHostnames)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(meowSshRelayHostnames);

        if (transport != SshTransport.Tailcat)
            return RelayClass.NotApplicable;

        if (details.EmbeddedRegions.Count == 0)
            return RelayClass.PublicDefault;

        var allowlist = meowSshRelayHostnames
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(NormalizeHostname)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hostnames = details.EmbeddedRegions
            .SelectMany(region => region.Nodes)
            .Select(node => node.HostName)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => NormalizeHostname(host!))
            .ToArray();

        // Embedded regions with no usable hostnames cannot be proven to belong
        // to the MeowSSH fleet. Treat them as foreign rather than accidentally
        // metering them because Enumerable.All on an empty sequence is true.
        if (hostnames.Length == 0)
            return RelayClass.SelfHosted;

        var meowSshCount = hostnames.Count(allowlist.Contains);
        if (meowSshCount == hostnames.Length)
            return RelayClass.MeowSsh;
        if (meowSshCount == 0)
            return RelayClass.SelfHosted;
        return RelayClass.Mixed;
    }

    private static string NormalizeHostname(string hostname) =>
        hostname.Trim().TrimEnd('.');
}
