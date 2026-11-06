using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Models;
using NotificationHub.Api.Persistence;
using NotificationHub.Api.Services;

namespace NotificationHub.Api.Workers;

public sealed class DeliveryProcessor(IDeliveryStore store, ISharedLimiter limiter, ISecretProtector secrets,
    IProtocolAdapter adapter, IHttpDelivery http, IOptions<HubOptions> options)
{
    public async Task<bool> ProcessOne(CancellationToken ct)
    {
        var task = await store.Claim(ct);
        if (task is null) return false;
        var owner = task.ClaimToken!.Value.ToString();
        var tenantKey = $"tenant:{task.TenantId}";
        var connectionKey = $"connection:{task.ConnectionId}";
        var tenantSlot = false; var connectionSlot = false;
        try
        {
            var context = await store.Load(task.Id, task.ClaimToken.Value, ct);
            if (context is null) { await store.Defer(task, 5, ct); return true; }
            tenantSlot = await limiter.Acquire(tenantKey, owner, options.Value.TenantConcurrency, options.Value.LeaseSeconds);
            if (!tenantSlot) { await store.Defer(task, 2, ct); return true; }
            connectionSlot = await limiter.Acquire(connectionKey, owner, options.Value.ConnectionConcurrency, options.Value.LeaseSeconds);
            if (!connectionSlot || !await limiter.Take(tenantKey, options.Value.TenantSendsPerMinute)
                || !await limiter.Take($"{connectionKey}:{task.Operation}", context.Definition.RequestsPerMinute))
            { await store.Defer(task, 2, ct); return true; }
            using var data = JsonDocument.Parse(context.Data);
            var credential = context.Connection.Credential is null ? null : secrets.Unprotect(context.Connection.Credential);
            var request = adapter.Build(context.Definition, data.RootElement, task.Id, credential);
            if (!await store.BeginAttempt(task, ct)) { await store.Defer(task, 5, ct); return true; }
            var outcome = await http.Send(request, context.Definition, task.ExpiresAt, ct);
            await store.Complete(task, context.Definition, outcome, ct);
            return true;
        }
        catch (Exception ex) when (ex is HubException or System.Security.Cryptography.CryptographicException)
        {
            var context = await store.Load(task.Id, task.ClaimToken.Value, ct);
            if (context is not null) await store.Complete(task, context.Definition, new(false, false, "configuration_error"), ct);
            return true;
        }
        finally
        {
            if (connectionSlot) await limiter.Release(connectionKey, owner);
            if (tenantSlot) await limiter.Release(tenantKey, owner);
        }
    }
}
public sealed class DeliveryWorker(IServiceScopeFactory scopes, IOptions<HubOptions> options, ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Enumerable.Range(0, options.Value.WorkerConcurrency).Select(_ => Loop(stoppingToken)));
    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (!await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessOne(ct)) await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Delivery worker failed; lease recovery will retry the task."); await Task.Delay(1000, ct); }
        }
    }
}
public sealed class RecoveryWorker(IDeliveryStore store, ILogger<RecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await store.Sweep(ct); await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Recovery sweep failed."); await Task.Delay(1000, ct); }
        }
    }
}
