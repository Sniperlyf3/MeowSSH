using System.Text.RegularExpressions;

namespace MeowSSH.Core.Diagnostics;

/// <summary>
/// Removes common secret and user-identifying values before a diagnostic record
/// can be presented for consent or leave the device.
/// </summary>
public static class DiagnosticSanitizer
{
    public const int MaxTextLength = 16_384;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex PrivateKey = new(
        @"-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----[\s\S]*?-----END(?: [A-Z0-9]+)* PRIVATE KEY-----",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex BearerToken = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex NamedSecret = new(
        @"\b(password|passwd|passphrase|token|secret|api[_-]?key|private[_-]?key)\s*[:=]\s*([^\s,;]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex NamedIdentity = new(
        @"\b(user(?:name)?|host(?:name)?)\s*[:=]\s*([^\s,;]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex Uri = new(
        @"\b(?:https?|ssh|sftp)://[^\s]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex Email = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex Ipv4 = new(
        @"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex WindowsPath = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:\\(?:[^\s\\/:*?\""<>|]+\\)*[^\s\\/:*?\""<>|]*",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex UnixHomePath = new(
        @"(?<![A-Za-z0-9])/(?:home|Users|data|storage|sdcard)/[^\s:]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    public static string SanitizeText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var result = value.Length > MaxTextLength
            ? value[..MaxTextLength] + "…[truncated]"
            : value;

        result = PrivateKey.Replace(result, "[redacted-private-key]");
        result = BearerToken.Replace(result, "Bearer [redacted]");
        result = NamedSecret.Replace(result, match => $"{match.Groups[1].Value}=[redacted]");
        result = NamedIdentity.Replace(result, match => $"{match.Groups[1].Value}=[redacted]");
        result = Uri.Replace(result, "[redacted-uri]");
        result = Email.Replace(result, "[redacted-email]");
        result = Ipv4.Replace(result, "[redacted-ip]");
        result = WindowsPath.Replace(result, "[redacted-path]");
        result = UnixHomePath.Replace(result, "[redacted-path]");
        return result;
    }
}
