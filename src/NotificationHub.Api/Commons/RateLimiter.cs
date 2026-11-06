using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NotificationHub.Api.Commons;

public interface ISharedLimiter
{
    Task<bool> Take(string key, int perMinute);
    Task<bool> Acquire(string key, string owner, int limit, int seconds);
    Task Release(string key, string owner);
}
public sealed class RedisLimiter(IConnectionMultiplexer redis) : ISharedLimiter
{
    private const string Bucket = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) + tonumber(t[2]) / 1000000
        local cap = tonumber(ARGV[1])
        local v = redis.call('HMGET', KEYS[1], 'tokens', 'at')
        local tokens = math.min(cap, (tonumber(v[1]) or cap) + math.max(0, now - (tonumber(v[2]) or now)) * cap / 60)
        local ok = 0
        if tokens >= 1 then tokens = tokens - 1; ok = 1 end
        redis.call('HSET', KEYS[1], 'tokens', tokens, 'at', now)
        redis.call('EXPIRE', KEYS[1], 120)
        return ok
        """;
    private const string Semaphore = """
        local t = redis.call('TIME')
        local now = tonumber(t[1])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then return 0 end
        redis.call('ZADD', KEYS[1], now + tonumber(ARGV[3]), ARGV[1])
        redis.call('EXPIRE', KEYS[1], tonumber(ARGV[3]) + 1)
        return 1
        """;
    public async Task<bool> Take(string key, int perMinute) =>
        (int)await redis.GetDatabase().ScriptEvaluateAsync(Bucket, [(RedisKey)$"hub:rate:{key}"], [(RedisValue)perMinute]) == 1;
    public async Task<bool> Acquire(string key, string owner, int limit, int seconds) =>
        (int)await redis.GetDatabase().ScriptEvaluateAsync(Semaphore, [(RedisKey)$"hub:slots:{key}"], [owner, limit, seconds]) == 1;
    public async Task Release(string key, string owner) => await redis.GetDatabase().SortedSetRemoveAsync($"hub:slots:{key}", owner);
}
public sealed class IngressMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISharedLimiter limiter, IOptions<HubOptions> options)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var id = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value;
            try
            {
                var lane = HttpMethods.IsGet(context.Request.Method) ? "read" : "write";
                if (!await limiter.Take($"ingress:{id}:{lane}", options.Value.IngressPerMinute))
                    throw new HubException("quota_exceeded", 429, 1);
            }
            catch (RedisException) { throw new HubException("unavailable", 503, 5); }
        }
        await next(context);
    }
}
