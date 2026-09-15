namespace MeowSSH.Core.Licensing;

public sealed class EntitlementService : IEntitlementService, IDisposable
{
    private readonly IEntitlementGrantProvider _provider;
    private readonly IEntitlementCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private EntitlementSnapshot _current;
    private bool _disposed;

    public EntitlementService(
        IEntitlementGrantProvider provider,
        IEntitlementCache cache,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = EntitlementSnapshot.Free(_timeProvider.GetUtcNow());
    }

    public EntitlementSnapshot Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_stateGate) return _current;
        }
    }

    public event EventHandler? Changed;

    public bool Has(PremiumFeature feature)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EntitlementPolicy.Allows(Current, feature, _timeProvider.GetUtcNow());
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cached = await _cache.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (cached is null || cached.IsExpired(_timeProvider.GetUtcNow())) return;
        SetCurrent(cached);
    }

    public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return UpdateAsync(() => _provider.RefreshAsync(cancellationToken), cancellationToken);
    }

    public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return UpdateAsync(() => _provider.RestorePurchasesAsync(cancellationToken), cancellationToken);
    }

    private async Task<EntitlementSnapshot> UpdateAsync(
        Func<Task<EntitlementSnapshot>> operation,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entitlement = await operation().ConfigureAwait(false);
            if (entitlement.IsExpired(_timeProvider.GetUtcNow()))
                entitlement = EntitlementSnapshot.Free(_timeProvider.GetUtcNow());

            await _cache.SaveAsync(entitlement, cancellationToken).ConfigureAwait(false);
            SetCurrent(entitlement);
            return entitlement;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void SetCurrent(EntitlementSnapshot entitlement)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _current != entitlement;
            _current = entitlement;
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshGate.Dispose();
    }
}
