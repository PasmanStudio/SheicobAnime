using System.Collections.Concurrent;
using System.Text.Json;
using AnimeIndex.Api.Infrastructure.Cache;

namespace AnimeIndex.Api.Tests;

public class FakeCacheService : ICacheService
{
    private readonly ConcurrentDictionary<string, string> _store = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (_store.TryGetValue(key, out var json))
            return Task.FromResult(JsonSerializer.Deserialize<T>(json, JsonOptions));
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        _store[key] = JsonSerializer.Serialize(value, JsonOptions);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
}
