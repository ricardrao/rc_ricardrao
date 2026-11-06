using Dapper;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Models;
using NotificationHub.Api.Persistence;

namespace NotificationHub.Api.Services;

public interface IOperationsService
{
    Task<IReadOnlyList<TicketRow>> Tickets(CancellationToken ct);
    Task<object> Ticket(Guid id, CancellationToken ct);
    Task Resolve(Guid id, ResolveTicketRequest request, CancellationToken ct);
    Task<IReadOnlyList<SubscriptionRow>> Subscriptions(CancellationToken ct);
    Task<SubscriptionRow> Subscribe(SubscribeRequest request, CancellationToken ct);
    Task Unsubscribe(Guid id, CancellationToken ct);
    Task<object> Metrics(CancellationToken ct);
    Task<object> Outbox(CancellationToken ct);
    Task RetryOutbox(Guid id, ActionRequest request, CancellationToken ct);
}
public sealed class OperationsService(Database database, ICaller caller, IUrlPolicy urls, ISecretProtector secrets) : IOperationsService
{
    public async Task<IReadOnlyList<TicketRow>> Tickets(CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        return (await db.QueryAsync<TicketRow>("SELECT * FROM tickets WHERE tenant_id=@tenant OR @admin ORDER BY updated_at DESC LIMIT 200", new { tenant = caller.TenantId, admin = caller.Role == "admin" })).AsList();
    }
    public async Task<object> Ticket(Guid id, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        var ticket = await db.QuerySingleOrDefaultAsync<TicketRow>("SELECT * FROM tickets WHERE id=@id AND (tenant_id=@tenant OR @admin)", new { id, tenant = caller.TenantId, admin = caller.Role == "admin" }) ?? throw new HubException("not_found", 404);
        var deliveries = await db.QueryAsync<Guid>("SELECT delivery_id FROM ticket_deliveries WHERE ticket_id=@id ORDER BY delivery_id LIMIT 200", new { id });
        return new { ticket, deliveryIds = deliveries };
    }
    public async Task Resolve(Guid id, ResolveTicketRequest request, CancellationToken ct)
    {
        if (request.Resolution is not ("fixed" or "acknowledged" or "no_action")) throw new HubException("invalid_resolution");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length is < 3 or > 1000) throw new HubException("invalid_request");
        await using var db = await database.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var row = await db.QuerySingleOrDefaultAsync<TicketRow>("SELECT * FROM tickets WHERE id=@id AND (tenant_id=@tenant OR @admin) FOR UPDATE", new { id, tenant = caller.TenantId, admin = caller.Role == "admin" }, tx) ?? throw new HubException("not_found", 404);
        if (row.Status != "open") throw new HubException("conflict", 409);
        await db.ExecuteAsync("""
            UPDATE tickets SET status='closed',resolution=@Resolution,updated_at=clock_timestamp() WHERE id=@id;
            INSERT INTO audit_entries(id,tenant_id,resource_id,action,reason) VALUES(@audit,@actor,@id,'resolve_ticket',@Reason)
            """, new { id, request.Resolution, request.Reason, audit = Guid.NewGuid(), actor = caller.TenantId }, tx);
        await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<SubscriptionRow>> Subscriptions(CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        return (await db.QueryAsync<SubscriptionRow>("SELECT * FROM subscriptions WHERE tenant_id=@tenant ORDER BY id LIMIT 200", new { tenant = caller.TenantId })).AsList();
    }
    public async Task<SubscriptionRow> Subscribe(SubscribeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.SigningSecret) || request.SigningSecret.Length is < 32 or > 8192) throw new HubException("invalid_request");
        await urls.Validate(request.Url, ct);
        await using var db = await database.Open(ct);
        return await db.QuerySingleAsync<SubscriptionRow>("INSERT INTO subscriptions(id,tenant_id,url,signing_secret) VALUES(@id,@tenant,@Url,@secret) RETURNING *",
            new { id = Guid.NewGuid(), tenant = caller.TenantId, request.Url, secret = secrets.Protect(request.SigningSecret) });
    }
    public async Task Unsubscribe(Guid id, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        if (await db.ExecuteAsync("UPDATE subscriptions SET enabled=false WHERE id=@id AND tenant_id=@tenant", new { id, tenant = caller.TenantId }) == 0) throw new HubException("not_found", 404);
    }
    public async Task<object> Metrics(CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        var tenant = caller.TenantId; var admin = caller.Role == "admin";
        var states = await db.QueryAsync("SELECT status,count(*) AS count FROM deliveries WHERE tenant_id=@tenant OR @admin GROUP BY status", new { tenant, admin });
        var oldest = await db.ExecuteScalarAsync<double?>("SELECT extract(epoch FROM clock_timestamp()-min(created_at))::double precision FROM deliveries WHERE (tenant_id=@tenant OR @admin) AND status IN ('pending','processing','retry_wait')", new { tenant, admin });
        var outboxStopped = await db.ExecuteScalarAsync<int>("SELECT count(*) FROM outbox WHERE status='stopped' AND (tenant_id=@tenant OR @admin)", new { tenant, admin });
        return new { deliveries = states, oldestPendingSeconds = oldest ?? 0, stoppedOutboxCount = outboxStopped };
    }
    public async Task<object> Outbox(CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        return await db.QueryAsync("SELECT id,kind,status,attempts,last_error,created_at FROM outbox WHERE tenant_id=@tenant OR @admin ORDER BY created_at DESC LIMIT 200", new { tenant = caller.TenantId, admin = caller.Role == "admin" });
    }
    public async Task RetryOutbox(Guid id, ActionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length is < 3 or > 1000) throw new HubException("invalid_request");
        await using var db = await database.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await db.ExecuteAsync("UPDATE outbox SET status='pending',attempts=0,next_attempt_at=clock_timestamp(),claim_token=NULL,lease_until=NULL WHERE id=@id AND status='stopped' AND (tenant_id=@tenant OR @admin)",
            new { id, tenant = caller.TenantId, admin = caller.Role == "admin" }, tx) == 0) throw new HubException("conflict", 409);
        await db.ExecuteAsync("INSERT INTO audit_entries(id,tenant_id,resource_id,action,reason) VALUES(@audit,@tenant,@id,'retry_outbox',@Reason)", new { audit = Guid.NewGuid(), tenant = caller.TenantId, id, request.Reason }, tx);
        await tx.CommitAsync(ct);
    }
}
