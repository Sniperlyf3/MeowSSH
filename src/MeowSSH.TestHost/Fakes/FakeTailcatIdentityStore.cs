using MeowSSH.Core.Services;

namespace MeowSSH.TestHost.Fakes;

public sealed class FakeTailcatIdentityStore : ITailcatIdentityStore
{
    private readonly Dictionary<string, string> _identities = new(StringComparer.Ordinal)
    {
        ["client-default"] = "{\"Private\":\"privkey:test\"}",
    };

    public Task<string> ExportAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_identities.TryGetValue(name, out var json))
            throw new FileNotFoundException($"Tailcat identity '{name}' does not exist.");
        return Task.FromResult(json);
    }

    public Task ImportAsync(string name, string privateKeyJson, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_identities.ContainsKey(name) && !overwrite)
            throw new IOException($"Tailcat identity '{name}' already exists.");
        _identities[name] = privateKeyJson;
        return Task.CompletedTask;
    }
}
