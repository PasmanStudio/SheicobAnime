using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AnimeIndex.Api.Infrastructure.Cache;

/// <summary>
/// Two-tier cache: an in-process L1 (IMemoryCache) in front of a Redis L2 (Upstash).
///
/// Design goals after the June 2026 Upstash quota incident:
///   1. Redis is a SOFT dependency. Any Redis failure (over-quota, timeout, down)
///      degrades to a cache MISS so the caller falls back to Postgres — it never
///      surfaces as a 500 and never takes the site down.
///   2. The L1 layer absorbs the vast majority of hot reads, so a burst of traffic
///      no longer translates into one Redis command per request.
///   3. A circuit breaker stops calling Redis for a cooldown window after a failure,
///      so we don't pay the latency of a failing round-trip on every request.
/// </summary>
public sealed class RedisCacheService : ICacheService
{
    private readonly IDatabase _db;
    private readonly IMemoryCache _l1;
    private readonly ILogger<RedisCacheService> _logger;

    // L1 entries are short-lived so staleness stays bounded even across instances
    // (RemoveAsync only evicts the local L1). Catalog data already tolerates minutes
    // of staleness, so capping L1 at 60s is safe and still kills most Redis traffic.
    private static readonly TimeSpan MaxL1Lifetime = TimeSpan.FromSeconds(60);

    // After a Redis failure, skip Redis entirely for this window.
    private static readonly TimeSpan BreakerCooldown = TimeSpan.FromSeconds(30);
    private long _breakerOpenUntilTicks; // DateTimeOffset.UtcNow.Ticks; 0 = closed

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public RedisCacheService(IConnectionMultiplexer redis, IMemoryCache l1, ILogger<RedisCacheService> logger)
    {
        _db = redis.GetDatabase();
        _l1 = l1;
        _logger = logger;
    }

    private bool RedisAvailable => Interlocked.Read(ref _breakerOpenUntilTicks) < DateTimeOffset.UtcNow.Ticks;

    private void TripBreaker(Exception ex)
    {
        Interlocked.Exchange(ref _breakerOpenUntilTicks, DateTimeOffset.UtcNow.Add(BreakerCooldown).Ticks);
        _logger.LogWarning(ex,
            "Redis unavailable — serving from L1/Postgres and skipping Redis for {Cooldown}s",
            BreakerCooldown.TotalSeconds);
    }

    private static string L1Key(string key) => "l1:" + key;

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        // L1: in-process, zero Redis commands.
        if (_l1.TryGetValue(L1Key(key), out T? l1Value))
        {
            _logger.LogDebug("Cache L1 HIT {CacheKey}", key);
            return l1Value;
        }

        if (!RedisAvailable)
            return default; // breaker open → treat as miss, go to Postgres

        try
        {
            var value = await _db.StringGetAsync(key);
            if (value.HasValue)
            {
                _logger.LogDebug("Cache L2 HIT {CacheKey}", key);
                var deserialized = JsonSerializer.Deserialize<T>(value!, JsonOptions);
                _l1.Set(L1Key(key), deserialized, MaxL1Lifetime);
                return deserialized;
            }
            _logger.LogDebug("Cache MISS {CacheKey}", key);
            return default;
        }
        catch (Exception ex)
        {
            TripBreaker(ex);
            return default; // degrade to miss — caller reads from Postgres
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        // Always populate L1 so the next read is served locally even if Redis is down.
        var l1Expiry = expiry.HasValue && expiry.Value < MaxL1Lifetime ? expiry.Value : MaxL1Lifetime;
        _l1.Set(L1Key(key), value, l1Expiry);

        if (!RedisAvailable)
            return;

        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            await _db.StringSetAsync(key, json, expiry);
        }
        catch (Exception ex)
        {
            TripBreaker(ex);
            // swallow — cache writes are best-effort
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _l1.Remove(L1Key(key));

        if (!RedisAvailable)
            return;

        try
        {
            await _db.KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            TripBreaker(ex);
            // swallow
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (!RedisAvailable)
            return false;

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
