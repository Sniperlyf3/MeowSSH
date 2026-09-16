using MeowSSH.Core.Diagnostics;

namespace MeowSSH.Core.Tests.Diagnostics;

public sealed class DiagnosticSanitizerTests
{
    [Fact]
    public void SanitizesCommonSecretsAndIdentifiers()
    {
        const string input = """
            host=prod.example.com username=alice password=hunter2 token=abc123
            Connect ssh://alice@10.20.30.40:22/home/alice/project
            contact alice@example.com from 192.168.1.22
            Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature
            file C:\Users\Alice\secret.txt and /home/alice/.ssh/id_ed25519
            -----BEGIN OPENSSH PRIVATE KEY-----
            extremely-secret-key-material
            -----END OPENSSH PRIVATE KEY-----
            """;

        var result = DiagnosticSanitizer.SanitizeText(input);

        Assert.DoesNotContain("prod.example.com", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("10.20.30.40", result, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.22", result, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGci", result, StringComparison.Ordinal);
        Assert.DoesNotContain("extremely-secret-key-material", result, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\Alice\\secret.txt", result, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/alice/.ssh/id_ed25519", result, StringComparison.Ordinal);
        Assert.Contains("[redacted-private-key]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsDiagnosticTextBeforeSubmission()
    {
        var input = new string('x', DiagnosticSanitizer.MaxTextLength + 5000);

        var result = DiagnosticSanitizer.SanitizeText(input);

        Assert.True(result.Length < input.Length);
        Assert.EndsWith("…[truncated]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void NullAndEmptyInputStayEmpty()
    {
        Assert.Equal(string.Empty, DiagnosticSanitizer.SanitizeText(null));
        Assert.Equal(string.Empty, DiagnosticSanitizer.SanitizeText(string.Empty));
    }
}

public sealed class DiagnosticBreadcrumbBufferTests
{
    [Fact]
    public void KeepsOnlyTheNewestBoundedSemanticEvents()
    {
        var buffer = new DiagnosticBreadcrumbBuffer(3);
        var start = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

        buffer.Add(DiagnosticBreadcrumbKind.AppStarted, start);
        buffer.Add(DiagnosticBreadcrumbKind.OpenedSettings, start.AddSeconds(1));
        buffer.Add(DiagnosticBreadcrumbKind.OpenedSftp, start.AddSeconds(2));
        buffer.Add(DiagnosticBreadcrumbKind.StartedSftpDownload, start.AddSeconds(3));

        var snapshot = buffer.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(DiagnosticBreadcrumbKind.OpenedSettings, snapshot[0].Kind);
        Assert.Equal(DiagnosticBreadcrumbKind.StartedSftpDownload, snapshot[2].Kind);
    }

    [Fact]
    public void SnapshotIsIndependentAndClearRemovesPendingHistory()
    {
        var buffer = new DiagnosticBreadcrumbBuffer();
        buffer.Add(DiagnosticBreadcrumbKind.OpenedHostEditor);

        var snapshot = buffer.Snapshot();
        buffer.Clear();

        Assert.Single(snapshot);
        Assert.Empty(buffer.Snapshot());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void RejectsUnboundedCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticBreadcrumbBuffer(capacity));
    }
}
