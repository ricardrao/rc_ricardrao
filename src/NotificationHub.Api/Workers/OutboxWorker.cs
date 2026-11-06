using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Models;
using NotificationHub.Api.Persistence;

namespace NotificationHub.Api.Workers;

public sealed class OutboxDispatcher(Database database, IHttpClientFactory clients, IUrlPolicy urls,
    ISecretProtector secrets, IOptions<HubOptions> options, ILogger<OutboxDispatcher> logger)
{
    public async Task<bool> ProcessOne(CancellationToken ct)
    {
        await using var db = await database.Open(ct);
        await db.ExecuteAsync("UPDATE outbox SET status=CASE WHEN attempts>=@max THEN 'stopped' ELSE 'retry_wait' END,claim_token=NULL,lease_until=NULL,last_error='lease_expired' WHERE status='processing' AND lease_until<=clock_timestamp()", new { max = options.Value.OutboxMaxAttempts });
        var item = await db.QuerySingleOrDefaultAsync<OutboxRow>("""
            WITH pick AS (
              SELECT id FROM outbox WHERE status IN ('pending','retry_wait') AND next_attempt_at<=clock_timestamp()
              ORDER BY next_attempt_at,id FOR UPDATE SKIP LOCKED LIMIT 1
            ) UPDATE outbox o SET status='processing',attempts=attempts+1,claim_token=gen_random_uuid(),lease_until=clock_timestamp()+interval '60 seconds'
              FROM pick WHERE o.id=pick.id RETURNING o.*
            """);
        if (item is null) return false;
        var success = false; var error = "network_error";
        try
        {
            var url = options.Value.TicketWebhookUrl;
            var secret = options.Value.TicketWebhookSecret;
            if (item.SubscriptionId is Guid subscriptionId)
            {
                var subscription = await db.QuerySingleAsync<SubscriptionRow>("SELECT * FROM subscriptions WHERE id=@subscriptionId", new { subscriptionId });
                if (!subscription.Enabled) { await Finish("cancelled", null); return true; }
                url = subscription.Url; secret = secrets.Unprotect(subscription.SigningSecret);
            }
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(secret)) { error = "configuration_error"; }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await urls.Validate(url, timeout.Token);
                var body = JsonDefaults.Serialize(new { eventId = item.Id, type = item.Kind, data = JsonSerializer.Deserialize<JsonElement>(item.Payload) });
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Add("Idempotency-Key", item.Id.ToString());
                request.Headers.Add("X-Hub-Timestamp", timestamp);
                request.Headers.Add("X-Hub-Signature", signature);
                using var response = await clients.CreateClient("outbox").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                success = response.IsSuccessStatusCode;
                error = $"http_{(int)response.StatusCode}";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or HubException or System.Net.Sockets.SocketException or CryptographicException)
        { error = ex is HubException ? "unsafe_destination" : "network_error"; }
        var status = success ? "succeeded" : item.Attempts >= options.Value.OutboxMaxAttempts ? "stopped" : "retry_wait";
        await Finish(status, success ? null : error);
        if (status == "stopped") logger.LogError("Outbox delivery {EventId} exhausted retries. Inspect the outbox metrics and replay endpoint.", item.Id);
        return true;

        async Task Finish(string status, string? failure)
        {
            await db.ExecuteAsync("""
                UPDATE outbox SET status=@status,last_error=@failure,claim_token=NULL,lease_until=NULL,
                  next_attempt_at=clock_timestamp()+@delay*interval '1 second'
                WHERE id=@Id AND claim_token=@ClaimToken AND lease_until>clock_timestamp() AND status='processing'
                """, new { item.Id, item.ClaimToken, status, failure, delay = Math.Min(3600, 10 * Math.Pow(2, Math.Min(item.Attempts, 10))) });
        }
    }
}
public sealed class OutboxWorker(OutboxDispatcher dispatcher, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (!await dispatcher.ProcessOne(ct)) await Task.Delay(1000, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Outbox worker failed."); await Task.Delay(1000, ct); }
        }
    }
}
