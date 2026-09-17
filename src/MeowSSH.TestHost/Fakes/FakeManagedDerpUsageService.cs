using MeowSSH.Core.Licensing;

namespace MeowSSH.TestHost.Fakes;

public sealed class FakeManagedDerpUsageService : IManagedDerpUsageService
{
    public Task<ManagedDerpQuotaState> GetCurrentAsync(
        string clientNodePublic,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ManagedDerpQuotaState(
            clientNodePublic,
            EntitlementTier.Free,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            256UL * 1024,
            128UL * 1024,
            384UL * 1024,
            1024UL * 1024 * 1024,
            false));
    }
}
