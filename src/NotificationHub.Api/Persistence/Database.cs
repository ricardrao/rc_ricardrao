using Dapper;
using Npgsql;

namespace NotificationHub.Api.Persistence;

public sealed class Database(NpgsqlDataSource source)
{
    static Database()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new UtcTimestampHandler());
    }
    public ValueTask<NpgsqlConnection> Open(CancellationToken ct = default) => source.OpenConnectionAsync(ct);
    public async Task Migrate(string root, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync("SELECT pg_advisory_xact_lock(785542116)", transaction: tx);
        await db.ExecuteAsync(new CommandDefinition(await File.ReadAllTextAsync(Path.Combine(root, "Persistence/schema.sql"), ct),
            transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}

internal sealed class UtcTimestampHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime date => new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)),
        _ => throw new InvalidCastException("Expected a PostgreSQL timestamp.")
    };
    public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.Value = value.UtcDateTime;
        if (parameter is NpgsqlParameter pg) pg.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
    }
}
