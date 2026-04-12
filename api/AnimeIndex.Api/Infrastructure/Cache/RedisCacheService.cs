using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AnimeIndex.Api.Infrastructure.Cache;

public sealed class RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger) : ICacheService
{
    private readonly IDatabase _db = redis.GetDatabase();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var value = await _db.StringGetAsync(key);
        if (value.HasValue)
        {
            logger.LogDebug("Cache HIT {CacheKey}", key);
            return JsonSerializer.Deserialize<T>(value!, JsonOptions);
        }
        logger.LogDebug("Cache MISS {CacheKey}", key);
        return default;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await _db.StringSetAsync(key, json, expiry);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _db.KeyDeleteAsync(key);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var pong = await _db.PingAsync();
            return pong.TotalMilliseconds < 5000;
        }
        catch
        {
            return false;
        }
    }
}
