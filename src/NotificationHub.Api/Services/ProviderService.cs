using Dapper;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Models;
using NotificationHub.Api.Persistence;

namespace NotificationHub.Api.Services;

public interface IProviderService
{
    Task<IReadOnlyList<ProviderRow>> List(CancellationToken ct);
    Task<ProviderRow> Register(RegisterProviderRequest request, CancellationToken ct);
    Task<IReadOnlyList<VersionRow>> Versions(Guid providerId, CancellationToken ct);
    Task<VersionRow> AddVersion(Guid providerId, string operation, OperationDefinition request, CancellationToken ct);
    Task<PreparedRequest> Preview(Guid providerId, Guid versionId, PreviewRequest request, CancellationToken ct);
    Task Publish(Guid providerId, Guid versionId, CancellationToken ct);
    Task SetEnabled(Guid providerId, bool enabled, CancellationToken ct);
}
public sealed class ProviderService(Database database, ICaller caller, IProtocolAdapter adapter, IUrlPolicy urls) : IProviderService
{
    public async Task<IReadOnlyList<ProviderRow>> List(CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        return (await db.QueryAsync<ProviderRow>("SELECT * FROM providers WHERE enabled OR owner_id=@owner OR @admin ORDER BY created_at DESC LIMIT 200",
            new { owner = caller.TenantId, admin = caller.Role == "admin" })).AsList();
    }
    public async Task<ProviderRow> Register(RegisterProviderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100) throw new HubException("invalid_request");
        await using var db = await database.Open(ct);
        return await db.QuerySingleAsync<ProviderRow>("INSERT INTO providers(id,owner_id,name) VALUES(@id,@owner,@name) RETURNING *",
            new { id = Guid.NewGuid(), owner = caller.TenantId, name = request.Name });
    }
    public async Task<IReadOnlyList<VersionRow>> Versions(Guid providerId, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        var provider = await db.QuerySingleOrDefaultAsync<ProviderRow>("SELECT * FROM providers WHERE id=@providerId", new { providerId }) ?? throw new HubException("not_found", 404);
        return (await db.QueryAsync<VersionRow>("SELECT * FROM operation_versions WHERE provider_id=@providerId AND (status='published' OR @owner) ORDER BY operation,version DESC LIMIT 200",
            new { providerId, owner = provider.OwnerId == caller.TenantId || caller.Role == "admin" })).AsList();
    }
    public async Task<VersionRow> AddVersion(Guid providerId, string operation, OperationDefinition request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 100 || operation.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '-')))
            throw new HubException("invalid_request");
        adapter.Validate(request);
        await urls.Validate(request.Url, ct);
        await using var db = await database.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var provider = await db.QuerySingleOrDefaultAsync<ProviderRow>("SELECT * FROM providers WHERE id=@providerId FOR UPDATE", new { providerId }, tx);
        Own(provider);
        var next = await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(version),0)+1 FROM operation_versions WHERE provider_id=@providerId AND operation=@operation", new { providerId, operation }, tx);
        var result = await db.QuerySingleAsync<VersionRow>("INSERT INTO operation_versions(id,provider_id,operation,version,definition) VALUES(@id,@providerId,@operation,@next,CAST(@definition AS jsonb)) RETURNING *",
            new { id = Guid.NewGuid(), providerId, operation, next, definition = JsonDefaults.Serialize(request) }, tx);
        await tx.CommitAsync(ct);
        return result;
    }
    public async Task<PreparedRequest> Preview(Guid providerId, Guid versionId, PreviewRequest request, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        Own(await db.QuerySingleOrDefaultAsync<ProviderRow>("SELECT * FROM providers WHERE id=@providerId", new { providerId }));
        var version = await db.QuerySingleOrDefaultAsync<VersionRow>("SELECT * FROM operation_versions WHERE id=@versionId AND provider_id=@providerId", new { providerId, versionId }) ?? throw new HubException("not_found", 404);
        return adapter.Build(JsonDefaults.Read<OperationDefinition>(version.Definition), request.Data, Guid.Empty, "REDACTED");
    }
    public async Task Publish(Guid providerId, Guid versionId, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        Own(await db.QuerySingleOrDefaultAsync<ProviderRow>("SELECT * FROM providers WHERE id=@providerId", new { providerId }));
        var version = await db.QuerySingleOrDefaultAsync<VersionRow>("SELECT * FROM operation_versions WHERE id=@versionId AND provider_id=@providerId", new { providerId, versionId }) ?? throw new HubException("not_found", 404);
        var definition = JsonDefaults.Read<OperationDefinition>(version.Definition);
        adapter.Validate(definition); await urls.Validate(definition.Url, ct);
        await db.ExecuteAsync("UPDATE operation_versions SET status='published' WHERE id=@versionId", new { versionId });
    }
    public async Task SetEnabled(Guid providerId, bool enabled, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        Own(await db.QuerySingleOrDefaultAsync<ProviderRow>("SELECT * FROM providers WHERE id=@providerId", new { providerId }));
        await db.ExecuteAsync("UPDATE providers SET enabled=@enabled WHERE id=@providerId", new { providerId, enabled });
    }
    private void Own(ProviderRow? provider)
    {
        if (provider is null) throw new HubException("not_found", 404);
        if (provider.OwnerId != caller.TenantId && caller.Role != "admin") throw new HubException("forbidden", 403);
    }
}

public interface IConnectionService
{
    Task<IReadOnlyList<ConnectionRow>> List(CancellationToken ct);
    Task<ConnectionRow> Create(ConnectRequest request, CancellationToken ct);
    Task SetEnabled(Guid id, bool enabled, CancellationToken ct);
    Task RotateCredential(Guid id, CredentialRequest request, CancellationToken ct);
    Task<IReadOnlyList<HealthRow>> Health(Guid id, CancellationToken ct);
}
public sealed class ConnectionService(Database database, ICaller caller, ISecretProtector secrets) : IConnectionService
{
    public async Task<IReadOnlyList<ConnectionRow>> List(CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        return (await db.QueryAsync<ConnectionRow>("SELECT * FROM connections WHERE tenant_id=@tenant ORDER BY name LIMIT 200", new { tenant = caller.TenantId })).AsList();
    }
    public async Task<ConnectionRow> Create(ConnectRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || (request.Credential?.Length ?? 0) > 8192) throw new HubException("invalid_request");
        await using var db = await database.Open(ct);
        var provider = await db.QuerySingleOrDefaultAsync<ProviderRow>("SELECT * FROM providers WHERE id=@ProviderId", request) ?? throw new HubException("not_found", 404);
        if (!provider.Enabled) throw new HubException("inactive", 409);
        return await db.QuerySingleAsync<ConnectionRow>("INSERT INTO connections(id,tenant_id,provider_id,name,credential) VALUES(@id,@tenant,@ProviderId,@Name,@credential) RETURNING *",
            new { id = Guid.NewGuid(), tenant = caller.TenantId, request.ProviderId, request.Name, credential = request.Credential is null ? null : secrets.Protect(request.Credential) });
    }
    public async Task SetEnabled(Guid id, bool enabled, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        if (await db.ExecuteAsync("UPDATE connections SET enabled=@enabled WHERE id=@id AND tenant_id=@tenant", new { id, enabled, tenant = caller.TenantId }) == 0)
            throw new HubException("not_found", 404);
    }
    public async Task RotateCredential(Guid id, CredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Credential) || request.Credential.Length > 8192 || request.Credential.Contains('\r') || request.Credential.Contains('\n')) throw new HubException("invalid_request");
        await using var db = await database.Open(ct);
        if (await db.ExecuteAsync("UPDATE connections SET credential=@credential WHERE id=@id AND tenant_id=@tenant",
                new { id, tenant = caller.TenantId, credential = secrets.Protect(request.Credential) }) == 0) throw new HubException("not_found", 404);
    }
    public async Task<IReadOnlyList<HealthRow>> Health(Guid id, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM connections WHERE id=@id AND tenant_id=@tenant)", new { id, tenant = caller.TenantId })) throw new HubException("not_found", 404);
        return (await db.QueryAsync<HealthRow>("""
            SELECT h.*,
              (SELECT count(*) FROM delivery_attempts a JOIN deliveries d ON d.id=a.delivery_id
                WHERE d.connection_id=h.connection_id AND d.operation=h.operation AND a.finished_at>=clock_timestamp()-interval '10 minutes') AS recent_attempts,
              (SELECT count(*) FROM delivery_attempts a JOIN deliveries d ON d.id=a.delivery_id
                WHERE d.connection_id=h.connection_id AND d.operation=h.operation AND a.finished_at>=clock_timestamp()-interval '10 minutes' AND a.category<>'accepted') AS recent_failures,
              (SELECT count(*) FROM deliveries d WHERE d.connection_id=h.connection_id AND d.operation=h.operation
                AND d.status IN ('pending','processing','retry_wait')) AS pending_count
            FROM endpoint_health h WHERE h.connection_id=@id ORDER BY h.operation
            """, new { id })).AsList();
    }
}
