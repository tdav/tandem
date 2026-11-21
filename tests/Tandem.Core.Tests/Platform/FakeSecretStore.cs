using Tandem.Core.Platform;

namespace Tandem.Core.Tests.Platform;

/// <summary>An <see cref="ISecretStore"/> in memory. <see cref="Calls"/> records every call ("Get", "Set",
/// "Delete" with the key), cancelled ones too; while <see cref="Fail"/> is set, every call records itself and throws
/// it. Thread-safe.
/// </summary>
public sealed class FakeSecretStore : ISecretStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly List<(string Method, string Key)> _calls = [];

    /// <summary>The exception every later call throws; null lets the calls through.</summary>
    public Exception? Fail { get; set; }

    public IReadOnlyList<(string Method, string Key)> Calls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    /// <summary>The secrets stored, by key.</summary>
    public IReadOnlyDictionary<string, string> Secrets
    {
        get { lock (_lock) return new Dictionary<string, string>(_secrets, StringComparer.Ordinal); }
    }

    public Task<string?> GetAsync(string key, CancellationToken ct) => Run("Get", key, ct, () =>
        _secrets.TryGetValue(key, out var secret) ? secret : null);

    public Task SetAsync(string key, string secret, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Run<object?>("Set", key, ct, () =>
        {
            _secrets[key] = secret;
            return null;
        });
    }

    public Task DeleteAsync(string key, CancellationToken ct) => Run<object?>("Delete", key, ct, () =>
    {
        _secrets.Remove(key);
        return null;
    });

    private Task<T> Run<T>(string method, string key, CancellationToken ct, Func<T> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_lock)
        {
            _calls.Add((method, key));
            if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
            if (Fail is { } error) return Task.FromException<T>(error);
            return Task.FromResult(body());
        }
    }
}
