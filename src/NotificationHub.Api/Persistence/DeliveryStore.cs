using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Models;
using NotificationHub.Api.Services;

namespace NotificationHub.Api.Persistence;

public sealed record DeliveryContext(DeliveryRow Delivery, OperationDefinition Definition, ConnectionRow Connection, string Data);
public interface IDeliveryStore
{
    Task<DeliveryRow?> Claim(CancellationToken ct);
    Task<DeliveryContext?> Load(Guid id, Guid token, CancellationToken ct);
    Task<bool> BeginAttempt(DeliveryRow delivery, CancellationToken ct);
    Task Defer(DeliveryRow delivery, int seconds, CancellationToken ct);
    Task Complete(DeliveryRow delivery, OperationDefinition definition, DeliveryOutcome outcome, CancellationToken ct);
    Task Sweep(CancellationToken ct);
}
public sealed class DeliveryStore(Database database, IOptions<HubOptions> options) : IDeliveryStore
{
    public async Task<DeliveryRow?> Claim(CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        return await db.QuerySingleOrDefaultAsync<DeliveryRow>(new CommandDefinition("""
            WITH picked AS (
              SELECT d.id FROM deliveries d
              JOIN connections c ON c.id=d.connection_id JOIN providers p ON p.id=c.provider_id
              JOIN endpoint_health h ON h.connection_id=d.connection_id AND h.operation=d.operation
              WHERE d.status IN ('pending','retry_wait') AND d.next_attempt_at<=clock_timestamp()
                AND d.expires_at>clock_timestamp() AND c.enabled AND p.enabled
                AND (h.open_until IS NULL OR h.open_until<=clock_timestamp())
              ORDER BY d.next_attempt_at,d.id FOR UPDATE OF d SKIP LOCKED LIMIT 1
            ) UPDATE deliveries d SET status='processing',claim_token=gen_random_uuid(),
                lease_until=clock_timestamp()+@lease*interval '1 second'
              FROM picked WHERE d.id=picked.id RETURNING d.*
            """, new { lease = options.Value.LeaseSeconds }, cancellationToken: ct));
    }
    public async Task<DeliveryContext?> Load(Guid id, Guid token, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        var task = await db.QuerySingleOrDefaultAsync<DeliveryRow>("""
            SELECT d.* FROM deliveries d JOIN connections c ON c.id=d.connection_id JOIN providers p ON p.id=c.provider_id
            WHERE d.id=@id AND d.claim_token=@token AND d.status='processing' AND d.lease_until>clock_timestamp()
              AND d.expires_at>clock_timestamp() AND c.enabled AND p.enabled
            """, new { id, token });
        if (task is null) return null;
        var version = await db.QuerySingleAsync<VersionRow>("SELECT * FROM operation_versions WHERE id=@id", new { id = task.VersionId });
        var connection = await db.QuerySingleAsync<ConnectionRow>("SELECT * FROM connections WHERE id=@id", new { id = task.ConnectionId });
        var data = await db.ExecuteScalarAsync<string>("SELECT data FROM notifications WHERE id=@id", new { id = task.NotificationId });
        return new(task, JsonDefaults.Read<OperationDefinition>(version.Definition), connection, data!);
    }
    public async Task<bool> BeginAttempt(DeliveryRow delivery, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var task = await db.QuerySingleOrDefaultAsync<DeliveryRow>("SELECT * FROM deliveries WHERE id=@Id AND claim_token=@ClaimToken AND status='processing' AND lease_until>clock_timestamp() AND expires_at>clock_timestamp() FOR UPDATE", delivery, tx);
        if (task is null) return false;
        var enabled = await db.ExecuteScalarAsync<bool>("SELECT c.enabled AND p.enabled FROM connections c JOIN providers p ON p.id=c.provider_id WHERE c.id=@ConnectionId", task, tx);
        if (!enabled) return false;
        var health = await db.QuerySingleAsync<HealthRow>("SELECT * FROM endpoint_health WHERE connection_id=@ConnectionId AND operation=@Operation FOR UPDATE", task, tx);
        var now = await db.ExecuteScalarAsync<DateTimeOffset>("SELECT clock_timestamp()", transaction: tx);
        if (health.OpenUntil > now) return false;
        if (health.State is "open" or "recovering")
            await db.ExecuteAsync("UPDATE endpoint_health SET state='recovering',open_until=clock_timestamp()+@lease*interval '1 second' WHERE connection_id=@ConnectionId AND operation=@Operation",
                new { task.ConnectionId, task.Operation, lease = options.Value.LeaseSeconds }, tx);
        await db.ExecuteAsync("""
            UPDATE deliveries SET attempts=attempts+1,round_attempts=round_attempts+1 WHERE id=@Id;
            INSERT INTO delivery_attempts(id,delivery_id,claim_token) VALUES(@attempt,@Id,@ClaimToken)
            """, new { task.Id, task.ClaimToken, attempt = Guid.NewGuid() }, tx);
        await tx.CommitAsync(ct);
        return true;
    }
    public async Task Defer(DeliveryRow delivery, int seconds, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        await db.ExecuteAsync("""
            UPDATE deliveries SET status=CASE WHEN expires_at<=clock_timestamp() THEN 'expired' ELSE 'retry_wait' END,
              next_attempt_at=clock_timestamp()+@seconds*interval '1 second',claim_token=NULL,lease_until=NULL
            WHERE id=@Id AND claim_token=@ClaimToken AND status='processing' AND lease_until>clock_timestamp()
            """, new { delivery.Id, delivery.ClaimToken, seconds });
    }
    public async Task Complete(DeliveryRow delivery, OperationDefinition definition, DeliveryOutcome outcome, CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var task = await db.QuerySingleOrDefaultAsync<DeliveryRow>("SELECT * FROM deliveries WHERE id=@Id AND claim_token=@ClaimToken AND status='processing' AND lease_until>clock_timestamp() FOR UPDATE", delivery, tx);
        if (task is null) return;
        var now = await db.ExecuteScalarAsync<DateTimeOffset>("SELECT clock_timestamp()", transaction: tx);
        var next = now + RetryPolicy.Delay(task.RoundAttempts, outcome.RetryAfterSeconds);
        var status = outcome.Success ? "succeeded" : (task.ExpiresAt <= now || outcome.Category == "expired") ? "expired"
            : !outcome.Retryable || task.RoundAttempts >= definition.MaxAttempts ? "stopped"
            : next >= task.ExpiresAt ? "expired" : "retry_wait";
        await db.ExecuteAsync("""
            UPDATE delivery_attempts SET finished_at=clock_timestamp(),category=@Category,http_status=@HttpStatus,uncertain=@Uncertain
              WHERE claim_token=@ClaimToken;
            UPDATE deliveries SET status=@status,next_attempt_at=@next,last_error=@Category,uncertain=uncertain OR @Uncertain,
              claim_token=NULL,lease_until=NULL WHERE id=@Id
            """, new { task.Id, task.ClaimToken, status, next, outcome.Category, outcome.HttpStatus, outcome.Uncertain }, tx);
        await UpdateHealth(db, tx, task, outcome, ct);
        if (status == "stopped") await CreateTicket(db, tx, task,
            outcome.Category is "timeout" or "network_error" or "upstream_error" or "rate_limited" or "invalid_response" ? "endpoint_unavailable" : outcome.Category, ct);
        if (status == "expired") await Emit(db, tx, task.TenantId, "delivery.expired", new { deliveryId = task.Id }, ct);
        await tx.CommitAsync(ct);
    }
    private async Task UpdateHealth(NpgsqlConnection db, NpgsqlTransaction tx, DeliveryRow task, DeliveryOutcome outcome, CancellationToken ct)
    {
        if (!outcome.Success && outcome.Category is not ("authentication" or "timeout" or "network_error" or "upstream_error" or "rate_limited" or "invalid_response")) return;
        var h = await db.QuerySingleAsync<HealthRow>("SELECT * FROM endpoint_health WHERE connection_id=@ConnectionId AND operation=@Operation FOR UPDATE", task, tx);
        var previous = h.State;
        if (outcome.Success)
        {
            h.ConsecutiveFailures = 0;
            h.RecoverySuccesses++;
            h.State = previous is "open" or "recovering" && h.RecoverySuccesses < 2 ? "recovering" : "healthy";
        }
        else
        {
            h.ConsecutiveFailures++;
            h.RecoverySuccesses = 0;
            var recent = await db.QuerySingleAsync<(long Total, long Failed)>("""
                SELECT count(*) AS Total,count(*) FILTER(WHERE a.category IN ('authentication','timeout','network_error','upstream_error','rate_limited','invalid_response')) AS Failed
                FROM delivery_attempts a JOIN deliveries d ON d.id=a.delivery_id
                WHERE d.connection_id=@ConnectionId AND d.operation=@Operation AND a.finished_at>=clock_timestamp()-interval '10 minutes'
                """, task, tx);
            h.State = outcome.Category == "authentication" || previous is "open" or "recovering"
                || h.ConsecutiveFailures >= options.Value.CircuitFailureThreshold || (recent.Total >= 20 && recent.Failed * 1.0 / recent.Total >= 0.8)
                ? "open" : "degraded";
        }
        await db.ExecuteAsync("""
            UPDATE endpoint_health SET state=@State,consecutive_failures=@ConsecutiveFailures,recovery_successes=@RecoverySuccesses,
              open_until=CASE WHEN @State='open' THEN clock_timestamp()+@cooldown*interval '1 second' ELSE NULL END,
              last_success_at=CASE WHEN @success THEN clock_timestamp() ELSE last_success_at END,
              last_failure_at=CASE WHEN NOT @success THEN clock_timestamp() ELSE last_failure_at END
            WHERE connection_id=@ConnectionId AND operation=@Operation
            """, new { h.State, h.ConsecutiveFailures, h.RecoverySuccesses, h.ConnectionId, h.Operation, success = outcome.Success, cooldown = options.Value.CircuitCooldownSeconds }, tx);
        if (previous != h.State)
        {
            await Emit(db, tx, task.TenantId, "endpoint.health_changed", new { task.ConnectionId, task.Operation, previous, current = h.State, category = outcome.Category }, ct);
            await db.ExecuteAsync("UPDATE endpoint_health SET last_notice_at=clock_timestamp() WHERE connection_id=@ConnectionId AND operation=@Operation", task, tx);
        }
        if (h.State == "open") await CreateTicket(db, tx, task, outcome.Category == "authentication" ? "authentication" : "endpoint_unavailable", ct);
    }
    private async Task CreateTicket(NpgsqlConnection db, NpgsqlTransaction tx, DeliveryRow task, string category, CancellationToken ct)
    {
        var proposed = Guid.NewGuid();
        var ticket = await db.QuerySingleAsync<TicketRow>("""
            INSERT INTO tickets(id,tenant_id,connection_id,operation,category)
              VALUES(@proposed,@TenantId,@ConnectionId,@Operation,@category)
            ON CONFLICT(tenant_id,connection_id,operation,category) WHERE status='open'
              DO UPDATE SET updated_at=clock_timestamp() RETURNING *
            """, new { proposed, task.TenantId, task.ConnectionId, task.Operation, category }, tx);
        var added = await db.ExecuteAsync("INSERT INTO ticket_deliveries(ticket_id,delivery_id) VALUES(@id,@delivery) ON CONFLICT DO NOTHING", new { id = ticket.Id, delivery = task.Id }, tx);
        if (added > 0) await db.ExecuteAsync("UPDATE tickets SET affected_count=affected_count+1 WHERE id=@Id", ticket, tx);
        if (ticket.Id == proposed)
            await Emit(db, tx, task.TenantId, "ticket.created", new { ticketId = ticket.Id, task.ConnectionId, task.Operation, category, deliveryId = task.Id }, ct, externalTicket: true);
    }
    public async Task Emit(NpgsqlConnection db, NpgsqlTransaction tx, string tenant, string kind, object payload, CancellationToken ct, bool externalTicket = false)
    {
        var body = JsonDefaults.Serialize(payload);
        await db.ExecuteAsync("""
            INSERT INTO outbox(id,subscription_id,tenant_id,kind,payload)
              SELECT gen_random_uuid(),id,@tenant,@kind,CAST(@body AS jsonb) FROM subscriptions WHERE tenant_id=@tenant AND enabled
            """, new { tenant, kind, body }, tx);
        if (externalTicket && !string.IsNullOrWhiteSpace(options.Value.TicketWebhookUrl))
            await db.ExecuteAsync("INSERT INTO outbox(id,tenant_id,kind,payload) VALUES(@id,@tenant,@kind,CAST(@body AS jsonb))",
                new { id = Guid.NewGuid(), tenant, kind, body }, tx);
    }
    public async Task Sweep(CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var stale = (await db.QueryAsync<DeliveryRow>("""
            SELECT * FROM deliveries WHERE (status='processing' AND lease_until<=clock_timestamp())
              OR (status IN ('pending','retry_wait') AND expires_at<=clock_timestamp())
            ORDER BY id FOR UPDATE SKIP LOCKED LIMIT 100
            """, transaction: tx)).AsList();
        foreach (var task in stale)
        {
            var hadAttempt = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM delivery_attempts WHERE claim_token=@ClaimToken)", task, tx);
            var version = await db.ExecuteScalarAsync<string>("SELECT definition FROM operation_versions WHERE id=@VersionId", task, tx);
            var definition = JsonDefaults.Read<OperationDefinition>(version!);
            var expired = await db.ExecuteScalarAsync<bool>("SELECT @ExpiresAt<=clock_timestamp()", task, tx);
            var status = expired ? "expired" : task.RoundAttempts >= definition.MaxAttempts ? "stopped" : "retry_wait";
            await db.ExecuteAsync("""
                UPDATE delivery_attempts SET finished_at=clock_timestamp(),category='lease_expired',uncertain=true WHERE claim_token=@ClaimToken AND finished_at IS NULL;
                UPDATE deliveries SET status=@status,claim_token=NULL,lease_until=NULL,next_attempt_at=clock_timestamp()+interval '5 seconds',
                  uncertain=uncertain OR @hadAttempt,last_error=CASE WHEN @hadAttempt THEN 'lease_expired' ELSE last_error END WHERE id=@Id
                """, new { task.Id, task.ClaimToken, status, hadAttempt }, tx);
            if (status == "stopped") await CreateTicket(db, tx, task, "lease_expired", ct);
            if (status == "expired") await Emit(db, tx, task.TenantId, "delivery.expired", new { deliveryId = task.Id }, ct);
        }
        var reminders = (await db.QueryAsync<HealthRow>("""
            SELECT * FROM endpoint_health WHERE state IN ('open','recovering','degraded')
              AND last_notice_at<clock_timestamp()-@minutes*interval '1 minute'
            ORDER BY last_notice_at FOR UPDATE SKIP LOCKED LIMIT 100
            """, new { minutes = options.Value.HealthReminderMinutes }, tx)).AsList();
        foreach (var h in reminders)
        {
            var tenant = await db.ExecuteScalarAsync<string>("SELECT tenant_id FROM connections WHERE id=@ConnectionId", h, tx);
            await Emit(db, tx, tenant!, "endpoint.health_reminder", h, ct);
            await db.ExecuteAsync("UPDATE endpoint_health SET last_notice_at=clock_timestamp() WHERE connection_id=@ConnectionId AND operation=@Operation", h, tx);
        }
        await tx.CommitAsync(ct);
    }
}
