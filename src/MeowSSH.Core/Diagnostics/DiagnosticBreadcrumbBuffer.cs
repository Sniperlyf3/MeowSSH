namespace MeowSSH.Core.Diagnostics;

public enum DiagnosticBreadcrumbKind
{
    AppStarted,
    OpenedSettings,
    OpenedHostEditor,
    OpenedSftp,
    StartedSftpUpload,
    StartedSftpDownload,
    StartedSshSession,
    ClosedSshSession,
    PurchaseFlowStarted,
    RestorePurchasesStarted,
}

public sealed record DiagnosticBreadcrumb(
    DiagnosticBreadcrumbKind Kind,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Keeps a small in-memory history of predefined semantic actions. Callers cannot
/// attach arbitrary strings, preventing terminal text, commands, host names and
/// other user data from leaking into crash breadcrumbs by construction.
/// </summary>
public sealed class DiagnosticBreadcrumbBuffer
{
    public const int DefaultCapacity = 32;
    public const int MaxCapacity = 64;

    private readonly object _gate = new();
    private readonly Queue<DiagnosticBreadcrumb> _items;
    private readonly int _capacity;

    public DiagnosticBreadcrumbBuffer(int capacity = DefaultCapacity)
    {
        if (capacity is < 1 or > MaxCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity), $"Capacity must be between 1 and {MaxCapacity}.");

        _capacity = capacity;
        _items = new Queue<DiagnosticBreadcrumb>(capacity);
    }

    public void Add(DiagnosticBreadcrumbKind kind, DateTimeOffset? timestampUtc = null)
    {
        lock (_gate)
        {
            while (_items.Count >= _capacity)
                _items.Dequeue();
            _items.Enqueue(new DiagnosticBreadcrumb(kind, timestampUtc ?? DateTimeOffset.UtcNow));
        }
    }

    public IReadOnlyList<DiagnosticBreadcrumb> Snapshot()
    {
        lock (_gate)
            return [.. _items];
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
    }
}
