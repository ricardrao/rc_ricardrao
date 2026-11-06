using Dapper;
using NotificationHub.Api.Persistence;
using StackExchange.Redis;

namespace NotificationHub.Api.Services;

public interface IHealthService { Task<bool> Ready(CancellationToken ct); }
public sealed class HealthService(Database database, IConnectionMultiplexer redis) : IHealthService
{
    public async Task<bool> Ready(CancellationToken ct)
    {
        try
        {
            await using var db = await database.Open(ct);
            await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
            await redis.GetDatabase().PingAsync();
            return true;
        }
        catch { return false; }
    }
}
