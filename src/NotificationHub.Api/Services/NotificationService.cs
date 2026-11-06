using Dapper;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Models;
using NotificationHub.Api.Persistence;

namespace NotificationHub.Api.Services;

public interface INotificationService
{
    Task<NotificationPage> List(int page, int pageSize, string? status, CancellationToken ct);
    Task<NotificationReceipt> Submit(string key, SubmitNotificationRequest request, CancellationToken ct);
    Task<NotificationView> Get(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AttemptRow>> Attempts(Guid deliveryId, CancellationToken ct);
    Task Retry(Guid deliveryId, ActionRequest request, CancellationToken ct);
    Task Cancel(Guid deliveryId, ActionRequest request, CancellationToken ct);
    Task Confirm(Guid deliveryId, ActionRequest request, CancellationToken ct);
}
public sealed class NotificationService(Database database, ICaller caller, IProtocolAdapter adapter,
    ISecretProtector secrets, IOptions<HubOptions> options) : INotificationService
{
    public async Task<NotificationPage> List(int page, int pageSize, string? status, CancellationToken ct)
    {
        if (page is < 1 or > 100000 || pageSize is < 1 or > 100
            || (status is not null && status is not ("pending" or "processing" or "retry_wait" or "succeeded" or "stopped" or "expired" or "cancelled")))
            throw new HubException("invalid_request");
        await using var db = await database.Open(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var parameters = new { tenant = caller.TenantId, status, offset = (page - 1) * pageSize, pageSize };
        const string filter = "FROM notifications n WHERE n.tenant_id=@tenant AND (@status IS NULL OR EXISTS(SELECT 1 FROM deliveries d WHERE d.notification_id=n.id AND d.status=@status))";
        var total = await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) " + filter, parameters, tx, cancellationToken: ct));
        var rows = (await db.QueryAsync<NotificationRow>(new CommandDefinition("SELECT n.* " + filter + " ORDER BY n.created_at DESC,n.id DESC LIMIT @pageSize OFFSET @offset", parameters, tx, cancellationToken: ct))).AsList();
        var deliveries = rows.Count == 0 ? [] : (await db.QueryAsync<DeliveryRow>(new CommandDefinition("SELECT * FROM deliveries WHERE notification_id=ANY(@ids) AND tenant_id=@tenant ORDER BY created_at,id", new { ids = rows.Select(x => x.Id).ToArray(), tenant = caller.TenantId }, tx, cancellationToken: ct))).AsList();
        var grouped = deliveries.ToLookup(x => x.NotificationId);
        await tx.CommitAsync(ct);
        return new(rows.Select(row => new NotificationView(row, grouped[row.Id].ToArray())).ToArray(), page, pageSize, total);
    }
    public async Task<NotificationReceipt> Submit(string key, SubmitNotificationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || request.Targets is not { Count: > 0 and <= 20 }
            || request.Targets.Any(x => x is null || string.IsNullOrWhiteSpace(x.Operation))
            || request.Targets.Distinct().Count() != request.Targets.Count || string.IsNullOrWhiteSpace(request.EventType)
            || request.Data.ValueKind != System.Text.Json.JsonValueKind.Object) throw new HubException("invalid_request");
        var hash = JsonDefaults.Fingerprint(request with { Targets = request.Targets.OrderBy(x => x.ConnectionId).ThenBy(x => x.Operation, StringComparer.Ordinal).ToList() });
        await using var db = await database.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var tenant = caller.TenantId;
        await db.ExecuteAsync("INSERT INTO tenant_quotas(tenant_id) VALUES(@tenant) ON CONFLICT DO NOTHING", new { tenant }, tx);
        await db.ExecuteAsync("SELECT tenant_id FROM tenant_quotas WHERE tenant_id=@tenant FOR UPDATE", new { tenant }, tx);
        var previous = await db.QuerySingleOrDefaultAsync<NotificationRow>("SELECT * FROM notifications WHERE tenant_id=@tenant AND idempotency_key=@key", new { tenant, key }, tx);
        if (previous is not null)
        {
            if (previous.RequestHash != hash) throw new HubException("idempotency_conflict", 409);
            var existing = (await db.QueryAsync<DeliveryView>("""
                SELECT d.id,d.connection_id AS ConnectionId,d.operation,d.status,h.state AS Health,d.expires_at AS ExpiresAt
                FROM deliveries d JOIN endpoint_health h USING(connection_id,operation) WHERE notification_id=@id ORDER BY d.id
                """, new { id = previous.Id }, tx)).AsList();
            await tx.CommitAsync(ct);
            return new(previous.Id, "accepted", true, existing);
        }
        var now = await db.ExecuteScalarAsync<DateTimeOffset>("SELECT clock_timestamp()", transaction: tx);
        if (request.ExpiresAt <= now) throw new HubException("expired", 422);
        if (request.ExpiresAt > now.AddDays(7)) throw new HubException("invalid_request");
        var count = await db.ExecuteScalarAsync<int>("SELECT count(*) FROM deliveries WHERE tenant_id=@tenant AND status IN ('pending','processing','retry_wait')", new { tenant }, tx);
        if (count + request.Targets.Count > options.Value.MaxPendingPerTenant) throw new HubException("quota_exceeded", 429, 60);
        await db.ExecuteAsync("UPDATE tenant_quotas SET used=0,window_start=@now WHERE tenant_id=@tenant AND window_start <= @now - interval '1 minute'", new { tenant, now }, tx);
        if (await db.ExecuteAsync("UPDATE tenant_quotas SET used=used+@cost WHERE tenant_id=@tenant AND used+@cost<=@limit",
            new { tenant, cost = request.Targets.Count, limit = options.Value.NewDeliveriesPerMinute }, tx) == 0) throw new HubException("quota_exceeded", 429, 60);
        var id = Guid.NewGuid();
        await db.ExecuteAsync("INSERT INTO notifications(id,tenant_id,idempotency_key,request_hash,event_type,data) VALUES(@id,@tenant,@key,@hash,@EventType,CAST(@data AS jsonb))",
            new { id, tenant, key, hash, request.EventType, data = request.Data.GetRawText() }, tx);
        var deliveries = new List<DeliveryView>();
        foreach (var target in request.Targets)
        {
            var connection = await db.QuerySingleOrDefaultAsync<ConnectionRow>("SELECT c.* FROM connections c JOIN providers p ON p.id=c.provider_id WHERE c.id=@ConnectionId AND c.tenant_id=@tenant AND c.enabled AND p.enabled FOR SHARE OF c,p",
                new { target.ConnectionId, tenant }, tx) ?? throw new HubException("inactive", 409);
            var version = await db.QueryFirstOrDefaultAsync<VersionRow>("SELECT * FROM operation_versions WHERE provider_id=@ProviderId AND operation=@Operation AND status='published' ORDER BY version DESC LIMIT 1",
                new { connection.ProviderId, target.Operation }, tx) ?? throw new HubException("not_found", 404);
            var definition = JsonDefaults.Read<OperationDefinition>(version.Definition);
            if (definition.EventType != request.EventType) throw new HubException("invalid_mapping");
            var deliveryId = Guid.NewGuid();
            adapter.Build(definition, request.Data, deliveryId, connection.Credential is null ? null : secrets.Unprotect(connection.Credential));
            var expires = request.ExpiresAt ?? now.AddHours(definition.MaxAgeHours);
            if (expires > now.AddHours(definition.MaxAgeHours)) throw new HubException("invalid_request");
            await db.ExecuteAsync("INSERT INTO endpoint_health(connection_id,operation) VALUES(@ConnectionId,@Operation) ON CONFLICT DO NOTHING", target, tx);
            var health = await db.ExecuteScalarAsync<string>("SELECT state FROM endpoint_health WHERE connection_id=@ConnectionId AND operation=@Operation", target, tx) ?? "healthy";
            await db.ExecuteAsync("""
                INSERT INTO deliveries(id,notification_id,tenant_id,connection_id,version_id,operation,expires_at,dedup_until)
                VALUES(@deliveryId,@id,@tenant,@ConnectionId,@versionId,@Operation,@expires,@dedup)
                """, new { deliveryId, id, tenant, target.ConnectionId, versionId = version.Id, target.Operation, expires, dedup = now.AddHours(definition.IdempotencyRetentionHours) }, tx);
            deliveries.Add(new(deliveryId, target.ConnectionId, target.Operation, "pending", health, expires));
        }
        await tx.CommitAsync(ct);
        return new(id, "accepted", false, deliveries);
    }
    public async Task<NotificationView> Get(Guid id, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        var notification = await db.QuerySingleOrDefaultAsync<NotificationRow>("SELECT * FROM notifications WHERE id=@id AND tenant_id=@tenant", new { id, tenant = caller.TenantId }) ?? throw new HubException("not_found", 404);
        return new(notification, (await db.QueryAsync<DeliveryRow>("SELECT * FROM deliveries WHERE notification_id=@id ORDER BY created_at,id", new { id })).AsList());
    }
    public async Task<IReadOnlyList<AttemptRow>> Attempts(Guid deliveryId, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM deliveries WHERE id=@deliveryId AND tenant_id=@tenant)", new { deliveryId, tenant = caller.TenantId })) throw new HubException("not_found", 404);
        return (await db.QueryAsync<AttemptRow>("SELECT * FROM delivery_attempts WHERE delivery_id=@deliveryId ORDER BY started_at DESC LIMIT 200", new { deliveryId })).AsList();
    }
    public Task Retry(Guid deliveryId, ActionRequest request, CancellationToken ct) => Act(deliveryId, request, "retry", ct);
    public Task Cancel(Guid deliveryId, ActionRequest request, CancellationToken ct) => Act(deliveryId, request, "cancel", ct);
    public Task Confirm(Guid deliveryId, ActionRequest request, CancellationToken ct) => Act(deliveryId, request, "confirm", ct);
    private async Task Act(Guid id, ActionRequest request, string action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length is < 3 or > 1000) throw new HubException("invalid_request");
        await using var db = await database.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var tenant = caller.TenantId;
        // Keep admission and manual replay under the same tenant quota lock.
        await db.ExecuteAsync("SELECT tenant_id FROM tenant_quotas WHERE tenant_id=@tenant FOR UPDATE", new { tenant }, tx);
        var task = await db.QuerySingleOrDefaultAsync<DeliveryRow>("SELECT * FROM deliveries WHERE id=@id AND tenant_id=@tenant FOR UPDATE", new { id, tenant }, tx) ?? throw new HubException("not_found", 404);
        var now = await db.ExecuteScalarAsync<DateTimeOffset>("SELECT clock_timestamp()", transaction: tx);
        string status;
        if (action == "retry")
        {
            if (task.Status != "stopped") throw new HubException("conflict", 409);
            if (task.ExpiresAt <= now) throw new HubException("expired", 409);
            if (task.DedupUntil <= now) throw new HubException("unsafe_replay", 409);
            var pending = await db.ExecuteScalarAsync<int>("SELECT count(*) FROM deliveries WHERE tenant_id=@tenant AND status IN ('pending','processing','retry_wait')", new { tenant }, tx);
            if (pending >= options.Value.MaxPendingPerTenant) throw new HubException("quota_exceeded", 429, 60);
            status = "pending";
        }
        else if (action == "confirm")
        {
            if (task.Status != "stopped") throw new HubException("conflict", 409);
            status = "succeeded";
        }
        else
        {
            if (task.Status is "succeeded" or "expired" or "cancelled") throw new HubException("conflict", 409);
            status = "cancelled";
        }
        await db.ExecuteAsync("""
            UPDATE deliveries SET status=@status,claim_token=NULL,lease_until=NULL,next_attempt_at=now(),
              uncertain=uncertain OR status='processing',
              round_attempts=CASE WHEN @action='retry' THEN 0 ELSE round_attempts END,
              retry_round=retry_round+CASE WHEN @action='retry' THEN 1 ELSE 0 END WHERE id=@id;
            INSERT INTO audit_entries(id,tenant_id,resource_id,action,reason) VALUES(@audit,@tenant,@id,@action,@Reason)
            """, new { id, tenant, status, action, audit = Guid.NewGuid(), request.Reason }, tx);
        if (action == "cancel" && task.Status == "processing")
            await db.ExecuteAsync("UPDATE delivery_attempts SET finished_at=clock_timestamp(),category='cancelled_in_flight',uncertain=true WHERE claim_token=@ClaimToken AND finished_at IS NULL", task, tx);
        await tx.CommitAsync(ct);
    }
}
