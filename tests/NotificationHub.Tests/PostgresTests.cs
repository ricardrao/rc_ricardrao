using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Models;
using NotificationHub.Api.Persistence;
using NotificationHub.Api.Services;
using NotificationHub.Api.Workers;
using StackExchange.Redis;

namespace NotificationHub.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_POSTGRES"))) Skip = "Set TEST_POSTGRES to run real PostgreSQL tests."; }
}
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_REDIS"))) Skip = "Set TEST_REDIS to run real Redis tests."; }
}
[CollectionDefinition("Postgres", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
public sealed class PostgresFixture : IAsyncLifetime
{
    private string? admin;
    private string? name;
    private NpgsqlDataSource? source;
    public Database Database { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";
    public static string ProjectRoot
    {
        get
        {
            var path = new DirectoryInfo(AppContext.BaseDirectory);
            while (path is not null && !File.Exists(Path.Combine(path.FullName, "NotificationHub.sln"))) path = path.Parent;
            return path?.FullName ?? throw new InvalidOperationException("Solution root not found.");
        }
    }
    public async Task InitializeAsync()
    {
        admin = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrEmpty(admin)) return;
        name = "hub_test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(admin); await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE DATABASE {name}");
        var builder = new NpgsqlConnectionStringBuilder(admin) { Database = name };
        ConnectionString = builder.ConnectionString;
        source = NpgsqlDataSource.Create(ConnectionString); Database = new(source);
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await Database.Migrate(Path.Combine(ProjectRoot, "src/NotificationHub.Api"));
    }
    public async Task DisposeAsync()
    {
        if (source is null) return;
        await source.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(admin); await connection.OpenAsync();
        await connection.ExecuteAsync($"DROP DATABASE {name} WITH (FORCE)");
    }
}
public sealed record FakeCaller(string TenantId, string Role = "business") : ICaller;
public sealed class TestSecrets : ISecretProtector
{
    public string Protect(string value) => "protected:" + value;
    public string Unprotect(string value) => value[10..];
}
public sealed class TestUrls : IUrlPolicy { public Task Validate(string url, CancellationToken ct = default) => Task.CompletedTask; }

[Collection("Postgres"), Trait("Category", "Integration")]
public sealed class PostgresTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly FakeCaller caller = new("tenant-a");
    private readonly HubOptions settings = new();
    private Guid connectionId;
    private Guid providerId;
    private NotificationService Service => new(fixture.Database, caller, new ProtocolAdapter(), new TestSecrets(), Options.Create(settings));
    private DeliveryStore Store => new(fixture.Database, Options.Create(settings));
    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(fixture.ConnectionString)) return;
        await using var db = await fixture.Database.Open();
        await db.ExecuteAsync("TRUNCATE providers,tenant_quotas,subscriptions,notifications,audit_entries CASCADE");
        var providers = new ProviderService(fixture.Database, new FakeCaller("supplier", "provider"), new ProtocolAdapter(), new TestUrls());
        providerId = (await providers.Register(new("CRM A"), default)).Id;
        var version = await providers.AddVersion(providerId, "contact.update", TestData.Definition(), default);
        await providers.Publish(providerId, version.Id, default);
        connectionId = (await new ConnectionService(fixture.Database, caller, new TestSecrets()).Create(new(providerId, "Main", null), default)).Id;
    }
    public Task DisposeAsync() => Task.CompletedTask;
    [PostgresFact]
    public async Task Concurrent_submissions_create_one_notification_and_one_delivery()
    {
        var request = TestData.Request(connectionId);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Service.Submit("same-event", request, default)));
        Assert.Single(results.Select(x => x.Id).Distinct()); Assert.Single(results, x => !x.Duplicate);
        await using var db = await fixture.Database.Open();
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM deliveries"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT used FROM tenant_quotas"));
    }
    [PostgresFact]
    public async Task Same_key_with_different_content_conflicts()
    {
        await Service.Submit("event", TestData.Request(connectionId), default);
        var error = await Assert.ThrowsAsync<HubException>(() => Service.Submit("event", TestData.Request(connectionId) with { Data = TestData.Json("{\"contact_id\":\"c2\",\"status\":\"paid\"}") }, default));
        Assert.Equal("idempotency_conflict", error.Code);
    }
    [PostgresFact]
    public async Task Failed_multi_target_validation_rolls_back_notification_and_quota()
    {
        var request = TestData.Request(connectionId) with { Targets = [new(connectionId, "contact.update"), new(Guid.NewGuid(), "contact.update")] };
        await Assert.ThrowsAsync<HubException>(() => Service.Submit("event", request, default));
        await using var db = await fixture.Database.Open();
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM notifications"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COALESCE(sum(used),0) FROM tenant_quotas"));
    }
    [PostgresFact]
    public async Task Concurrent_quota_admission_never_exceeds_limit()
    {
        settings.NewDeliveriesPerMinute = 3;
        var results = await Task.WhenAll(Enumerable.Range(0, 15).Select(async i =>
        {
            try { await Service.Submit($"event-{i}", TestData.Request(connectionId), default); return true; }
            catch (HubException ex) { Assert.Equal("quota_exceeded", ex.Code); return false; }
        }));
        Assert.Equal(3, results.Count(x => x));
    }
    [PostgresFact]
    public async Task Many_workers_claim_disjoint_tasks()
    {
        for (var i = 0; i < 12; i++) await Service.Submit($"event-{i}", TestData.Request(connectionId), default);
        var claims = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Store.Claim(default)));
        Assert.Equal(12, claims.Count(x => x is not null));
        Assert.Equal(12, claims.Where(x => x is not null).Select(x => x!.Id).Distinct().Count());
    }
    [PostgresFact]
    public async Task Expired_lease_is_reclaimed_and_stale_result_cannot_overwrite()
    {
        await Service.Submit("event", TestData.Request(connectionId), default);
        var old = (await Store.Claim(default))!;
        Assert.True(await Store.BeginAttempt(old, default));
        await using var db = await fixture.Database.Open();
        await db.ExecuteAsync("UPDATE deliveries SET lease_until=now()-interval '1 second'");
        await Store.Sweep(default);
        await db.ExecuteAsync("UPDATE deliveries SET next_attempt_at=now()");
        var current = (await Store.Claim(default))!;
        Assert.NotEqual(old.ClaimToken, current.ClaimToken);
        Assert.True(await Store.BeginAttempt(current, default));
        await Store.Complete(current, TestData.Definition(), new(true, false, "accepted", 200), default);
        await Store.Complete(old, TestData.Definition(), new(false, false, "http_rejected", 400), default);
        Assert.Equal("succeeded", await db.ExecuteScalarAsync<string>("SELECT status FROM deliveries"));
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT uncertain FROM deliveries"));
    }
    [PostgresFact]
    public async Task Expiry_prevents_claim_and_manual_replay()
    {
        var receipt = await Service.Submit("event", TestData.Request(connectionId), default);
        await using var db = await fixture.Database.Open();
        await db.ExecuteAsync("UPDATE deliveries SET expires_at=now()-interval '1 second'");
        Assert.Null(await Store.Claim(default));
        await Store.Sweep(default);
        Assert.Equal("expired", await db.ExecuteScalarAsync<string>("SELECT status FROM deliveries"));
        await Assert.ThrowsAsync<HubException>(() => Service.Retry(receipt.Deliveries[0].Id, TestData.Reason, default));
    }
    [PostgresFact]
    public async Task Temporary_failure_schedules_retry_without_ticket()
    {
        await Service.Submit("event", TestData.Request(connectionId), default);
        var claim = (await Store.Claim(default))!;
        Assert.True(await Store.BeginAttempt(claim, default));
        await Store.Complete(claim, TestData.Definition(), new(false, true, "upstream_error", 503), default);
        await using var db = await fixture.Database.Open();
        Assert.Equal("retry_wait", await db.ExecuteScalarAsync<string>("SELECT status FROM deliveries"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM tickets"));
        Assert.Null(await Store.Claim(default));
    }
    [PostgresFact]
    public async Task Exhausted_failures_atomically_create_grouped_ticket_and_outbox()
    {
        settings.TicketWebhookUrl = "https://example.com/tickets";
        for (var i = 0; i < 2; i++)
        {
            await Service.Submit($"event-{i}", TestData.Request(connectionId), default);
            var claim = (await Store.Claim(default))!;
            Assert.True(await Store.BeginAttempt(claim, default));
            await Store.Complete(claim, TestData.Definition() with { MaxAttempts = 1 }, new(false, true, "upstream_error", 503), default);
        }
        await using var db = await fixture.Database.Open();
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM deliveries WHERE status='stopped'"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM tickets"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT affected_count FROM tickets"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM outbox WHERE kind='ticket.created'"));
    }
    [PostgresFact]
    public async Task Circuit_opens_notifies_tenant_and_allows_one_recovery_probe()
    {
        settings.CircuitFailureThreshold = 1;
        var operations = new OperationsService(fixture.Database, caller, new TestUrls(), new TestSecrets());
        await operations.Subscribe(new("https://example.com/health", new string('x', 32)), default);
        await Service.Submit("event", TestData.Request(connectionId), default);
        var claim = (await Store.Claim(default))!;
        Assert.True(await Store.BeginAttempt(claim, default));
        await Store.Complete(claim, TestData.Definition(), new(false, true, "upstream_error", 503), default);
        await using var db = await fixture.Database.Open();
        Assert.Equal("open", await db.ExecuteScalarAsync<string>("SELECT state FROM endpoint_health"));
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM outbox WHERE kind='endpoint.health_changed')"));
        Assert.Null(await Store.Claim(default));
        await Service.Submit("event-2", TestData.Request(connectionId), default);
        await db.ExecuteAsync("UPDATE endpoint_health SET open_until=now()-interval '1 second'; UPDATE deliveries SET next_attempt_at=now()");
        var first = (await Store.Claim(default))!; var second = (await Store.Claim(default))!;
        Assert.True(await Store.BeginAttempt(first, default));
        Assert.False(await Store.BeginAttempt(second, default));
    }
    [PostgresFact]
    public async Task Tenant_cannot_read_or_cancel_another_tenants_task()
    {
        var receipt = await Service.Submit("event", TestData.Request(connectionId), default);
        var other = new NotificationService(fixture.Database, new FakeCaller("other"), new ProtocolAdapter(), new TestSecrets(), Options.Create(settings));
        Assert.Equal(404, (await Assert.ThrowsAsync<HubException>(() => other.Get(receipt.Id, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<HubException>(() => other.Cancel(receipt.Deliveries[0].Id, TestData.Reason, default))).Status);
    }
    [PostgresFact]
    public async Task Manual_retry_preserves_delivery_id_and_history_but_starts_new_budget()
    {
        var receipt = await Service.Submit("event", TestData.Request(connectionId), default);
        var claim = (await Store.Claim(default))!; await Store.BeginAttempt(claim, default);
        await Store.Complete(claim, TestData.Definition(), new(false, false, "http_rejected", 400), default);
        await Service.Retry(claim.Id, TestData.Reason, default);
        var view = await Service.Get(receipt.Id, default);
        Assert.Equal(claim.Id, view.Deliveries[0].Id);
        Assert.Equal(1, view.Deliveries[0].Attempts); Assert.Equal(0, view.Deliveries[0].RoundAttempts); Assert.Equal(1, view.Deliveries[0].RetryRound);
        Assert.Single(await Service.Attempts(claim.Id, default));
    }
    [PostgresFact]
    public async Task Configuration_versions_are_pinned_and_credentials_are_not_serialized()
    {
        var receipt = await Service.Submit("event", TestData.Request(connectionId), default);
        var before = (await Service.Get(receipt.Id, default)).Deliveries[0].VersionId;
        var provider = new ProviderService(fixture.Database, new FakeCaller("supplier", "provider"), new ProtocolAdapter(), new TestUrls());
        var next = await provider.AddVersion(providerId, "contact.update", TestData.Definition() with { Encoding = "form" }, default);
        await provider.Publish(providerId, next.Id, default);
        Assert.Equal(before, (await Service.Get(receipt.Id, default)).Deliveries[0].VersionId);
        var json = JsonDefaults.Serialize(new ConnectionRow { Credential = "secret" });
        Assert.DoesNotContain("secret", json); Assert.DoesNotContain("credential", json);
    }
    [PostgresFact]
    public async Task Capability_description_is_persisted_published_and_visible_to_business_callers()
    {
        var provider = new ProviderService(fixture.Database, new FakeCaller("supplier", "provider"), new ProtocolAdapter(), new TestUrls());
        const string description = "Synchronize a contact after payment.\nUpdates the CRM contact status.";
        var version = await provider.AddVersion(providerId, "contact.update", TestData.Definition() with { Description = description }, default);
        await provider.Publish(providerId, version.Id, default);
        var reader = new ProviderService(fixture.Database, caller, new ProtocolAdapter(), new TestUrls());
        var versions = await reader.Versions(providerId, default);
        var saved = Assert.Single(versions, x => x.Id == version.Id);
        Assert.Equal(description, JsonDefaults.Read<OperationDefinition>(saved.Definition).Description);
        Assert.Null(JsonDefaults.Read<OperationDefinition>(Assert.Single(versions, x => x.Id != version.Id).Definition).Description);
    }
    [PostgresFact]
    public async Task Resolving_ticket_does_not_falsely_mark_delivery_successful()
    {
        await Service.Submit("event", TestData.Request(connectionId), default);
        var claim = (await Store.Claim(default))!; await Store.BeginAttempt(claim, default);
        await Store.Complete(claim, TestData.Definition(), new(false, false, "http_rejected", 400), default);
        var operations = new OperationsService(fixture.Database, caller, new TestUrls(), new TestSecrets());
        var ticket = Assert.Single(await operations.Tickets(default));
        await operations.Resolve(ticket.Id, new("fixed", "Input contract reviewed."), default);
        await using var db = await fixture.Database.Open();
        Assert.Equal("stopped", await db.ExecuteScalarAsync<string>("SELECT status FROM deliveries"));
        Assert.Equal("closed", await db.ExecuteScalarAsync<string>("SELECT status FROM tickets"));
    }
    [PostgresFact]
    public async Task Processor_maps_request_and_sends_stable_idempotency_key()
    {
        var receipt = await Service.Submit("event", TestData.Request(connectionId), default);
        var limiter = new Mock<ISharedLimiter>();
        limiter.Setup(x => x.Take(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(true);
        limiter.Setup(x => x.Acquire(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(true);
        var http = new Mock<IHttpDelivery>();
        http.Setup(x => x.Send(It.IsAny<PreparedRequest>(), It.IsAny<OperationDefinition>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryOutcome(true, false, "accepted", 200));
        var processor = new DeliveryProcessor(Store, limiter.Object, new TestSecrets(), new ProtocolAdapter(), http.Object, Options.Create(settings));
        Assert.True(await processor.ProcessOne(default));
        http.Verify(x => x.Send(It.Is<PreparedRequest>(r => r.Headers["Idempotency-Key"] == receipt.Deliveries[0].Id.ToString()), It.IsAny<OperationDefinition>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("succeeded", (await Service.Get(receipt.Id, default)).Deliveries[0].Status);
    }
    [PostgresFact]
    public async Task Outbox_retries_with_same_event_id_and_verifiable_signature()
    {
        var secret = new string('s', 32);
        var operations = new OperationsService(fixture.Database, caller, new TestUrls(), new TestSecrets());
        var subscription = await operations.Subscribe(new("https://example.com/events", secret), default);
        await using var db = await fixture.Database.Open();
        var eventId = Guid.NewGuid();
        await db.ExecuteAsync("INSERT INTO outbox(id,subscription_id,tenant_id,kind,payload) VALUES(@eventId,@subscriptionId,'tenant-a','endpoint.health_changed','{}')", new { eventId, subscriptionId = subscription.Id });
        var calls = 0;
        using var client = new HttpClient(new StubHandler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(eventId.ToString(), Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            var timestamp = Assert.Single(request.Headers.GetValues("X-Hub-Timestamp"));
            var body = await request.Content!.ReadAsStringAsync(ct);
            var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
            Assert.Equal(expected, Assert.Single(request.Headers.GetValues("X-Hub-Signature")));
            return new HttpResponseMessage(calls == 1 ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK);
        }));
        var dispatcher = new OutboxDispatcher(fixture.Database, new StubClientFactory(client), new TestUrls(), new TestSecrets(), Options.Create(settings), NullLogger<OutboxDispatcher>.Instance);
        Assert.True(await dispatcher.ProcessOne(default));
        Assert.Equal("retry_wait", await db.ExecuteScalarAsync<string>("SELECT status FROM outbox"));
        await db.ExecuteAsync("UPDATE outbox SET next_attempt_at=now()");
        Assert.True(await dispatcher.ProcessOne(default));
        Assert.Equal("succeeded", await db.ExecuteScalarAsync<string>("SELECT status FROM outbox"));
        Assert.Equal(2, calls);
    }
    [PostgresFact]
    public async Task Cancellation_fences_an_active_worker()
    {
        var receipt = await Service.Submit("event", TestData.Request(connectionId), default);
        var claim = (await Store.Claim(default))!; await Store.BeginAttempt(claim, default);
        await Service.Cancel(claim.Id, TestData.Reason, default);
        await Store.Complete(claim, TestData.Definition(), new(true, false, "accepted", 200), default);
        var task = (await Service.Get(receipt.Id, default)).Deliveries[0];
        Assert.Equal("cancelled", task.Status); Assert.True(task.Uncertain);
        Assert.Equal("cancelled_in_flight", Assert.Single(await Service.Attempts(claim.Id, default)).Category);
    }
    [PostgresFact]
    public async Task Disabled_connection_cannot_be_claimed()
    {
        await Service.Submit("event", TestData.Request(connectionId), default);
        await new ConnectionService(fixture.Database, caller, new TestSecrets()).SetEnabled(connectionId, false, default);
        Assert.Null(await Store.Claim(default));
    }
    [PostgresFact]
    public async Task Notification_listing_is_paged_filtered_and_tenant_scoped()
    {
        var first = await Service.Submit("first", TestData.Request(connectionId), default);
        await Service.Submit("second", TestData.Request(connectionId), default);
        await Service.Cancel(first.Deliveries[0].Id, TestData.Reason, default);
        var page = await Service.List(1, 1, null, default);
        Assert.Equal(2, page.Total); Assert.Single(page.Items);
        var filtered = await Service.List(1, 20, "cancelled", default);
        Assert.Equal(first.Id, Assert.Single(filtered.Items).Notification.Id);
        var other = new NotificationService(fixture.Database, new FakeCaller("other"), new ProtocolAdapter(), new TestSecrets(), Options.Create(settings));
        Assert.Empty((await other.List(1, 20, null, default)).Items);
        Assert.Empty((await Service.List(10, 20, null, default)).Items);
        await Assert.ThrowsAsync<HubException>(() => Service.List(0, 20, null, default));
        await Assert.ThrowsAsync<HubException>(() => Service.List(1, 101, null, default));
        await Assert.ThrowsAsync<HubException>(() => Service.List(1, 20, "invalid", default));
    }
    [PostgresFact]
    public async Task Health_summary_includes_recent_failure_rate_and_backlog()
    {
        await Service.Submit("event", TestData.Request(connectionId), default);
        var claim = (await Store.Claim(default))!; await Store.BeginAttempt(claim, default);
        await Store.Complete(claim, TestData.Definition(), new(false, true, "upstream_error", 503), default);
        var health = Assert.Single(await new ConnectionService(fixture.Database, caller, new TestSecrets()).Health(connectionId, default));
        Assert.Equal(1, health.RecentAttempts); Assert.Equal(1, health.RecentFailures);
        Assert.Equal(1.0, health.FailureRate); Assert.Equal(1, health.PendingCount);
    }
    [PostgresFact]
    public async Task Outbox_crash_recovery_stops_at_attempt_budget()
    {
        await using var db = await fixture.Database.Open();
        await db.ExecuteAsync("INSERT INTO outbox(id,tenant_id,kind,payload,status,attempts,lease_until,claim_token) VALUES(gen_random_uuid(),'tenant-a','ticket.created','{}','processing',@max,now()-interval '1 second',gen_random_uuid())", new { max = settings.OutboxMaxAttempts });
        using var client = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("Exhausted event must not be sent.")));
        var dispatcher = new OutboxDispatcher(fixture.Database, new StubClientFactory(client), new TestUrls(), new TestSecrets(), Options.Create(settings), NullLogger<OutboxDispatcher>.Instance);
        Assert.False(await dispatcher.ProcessOne(default));
        Assert.Equal("stopped", await db.ExecuteScalarAsync<string>("SELECT status FROM outbox"));
    }
}

[Trait("Category", "Integration")]
public sealed class RedisTests
{
    [RedisFact]
    public async Task Atomic_bucket_and_semaphore_enforce_limits_across_instances()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("TEST_REDIS")!);
        var a = new RedisLimiter(connection); var b = new RedisLimiter(connection);
        var key = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => (i % 2 == 0 ? a : b).Take(key, 5)));
        Assert.Equal(5, results.Count(x => x));
        var slots = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => a.Acquire(key, i.ToString(), 3, 60)));
        Assert.Equal(3, slots.Count(x => x));
        var owner = Array.FindIndex(slots, x => x).ToString();
        await b.Release(key, owner);
        Assert.True(await a.Acquire(key, "replacement", 3, 60));
    }
}
