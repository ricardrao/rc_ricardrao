using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Controllers;
using NotificationHub.Api.Persistence;
using NotificationHub.Api.Services;
using NotificationHub.Api.Workers;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
DefaultTypeMap.MatchNamesWithUnderscores = true;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 262144);
builder.Services.AddOptions<HubOptions>().Bind(builder.Configuration.GetSection("Hub"))
    .Validate(o => o.LeaseSeconds >= 60 && o.WorkerConcurrency is > 0 and <= 64
        && o.MaxPendingPerTenant > 0 && o.NewDeliveriesPerMinute > 0 && o.IngressPerMinute > 0
        && o.TenantSendsPerMinute > 0 && o.TenantConcurrency > 0 && o.ConnectionConcurrency > 0
        && o.CircuitFailureThreshold > 0 && o.CircuitCooldownSeconds >= 1 && o.OutboxMaxAttempts > 0
        && o.HealthReminderMinutes > 0, "Invalid hub limits.").ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Postgres connection string is required.")));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis connection string is required.")));
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<ISharedLimiter, RedisLimiter>();
builder.Services.AddSingleton<UrlPolicy>();
builder.Services.AddSingleton<IUrlPolicy>(sp => sp.GetRequiredService<UrlPolicy>());
builder.Services.AddSingleton<IProtocolAdapter, ProtocolAdapter>();
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddSingleton<MessageCatalog>();
builder.Services.AddSingleton<IDeliveryStore, DeliveryStore>();
builder.Services.AddSingleton<IHttpDelivery, HttpDelivery>();
builder.Services.AddSingleton<DeliveryProcessor>();
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddSingleton<IHealthService, HealthService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICaller, HttpCaller>();
builder.Services.AddScoped<IProviderService, ProviderService>();
builder.Services.AddScoped<IConnectionService, ConnectionService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IOperationsService, OperationsService>();
var keys = builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".local", "keys");
builder.Services.AddDataProtection().SetApplicationName("NotificationHub").PersistKeysToFileSystem(new DirectoryInfo(keys));
foreach (var name in new[] { "delivery", "outbox" })
    builder.Services.AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan)
        .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<UrlPolicy>().CreateHandler());
builder.Services.AddAuthentication("ApiKey").AddScheme<AuthenticationSchemeOptions, ApiKeyAuthentication>("ApiKey", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
    new BadRequestObjectResult(new { code = "invalid_request", message = context.HttpContext.RequestServices.GetRequiredService<MessageCatalog>().Get("invalid_request"), traceId = context.HttpContext.TraceIdentifier }));
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var cultures = new[] { new CultureInfo("en"), new CultureInfo("zh-CN") };
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = cultures; options.SupportedUICultures = cultures;
    options.RequestCultureProviders = [new QueryStringRequestCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
    options.ApplyCurrentCultureToResponseHeaders = true;
});
if (builder.Configuration.GetValue("Hub:RunWorkers", true))
{
    builder.Services.AddHostedService<DeliveryWorker>();
    builder.Services.AddHostedService<RecoveryWorker>();
    builder.Services.AddHostedService<OutboxWorker>();
}
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRequestLocalization();
app.UseMiddleware<ErrorMiddleware>();
app.UseAuthentication();
app.UseMiddleware<IngressMiddleware>();
app.UseAuthorization();
app.MapControllers();
if (app.Services.GetRequiredService<IOptions<HubOptions>>().Value.MigrateOnStartup)
    await app.Services.GetRequiredService<Database>().Migrate(app.Environment.ContentRootPath);
app.Run();

public partial class Program;
