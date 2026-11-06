using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace NotificationHub.Api.Commons;

public sealed class HubException(string code, int status = 400, int? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public int? RetryAfter { get; } = retryAfter;
}
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new HubException("invalid_request");
    public static string Fingerprint<T>(T value)
    {
        var node = JsonSerializer.SerializeToElement(value, Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Canonical(writer, node);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Canonical(Utf8JsonWriter writer, JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var item in node.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(item.Name); Canonical(writer, item.Value); }
            writer.WriteEndObject();
        }
        else if (node.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in node.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); }
        else node.WriteTo(writer);
    }
}
public interface ICaller { string TenantId { get; } string Role { get; } }
public sealed class HttpCaller(IHttpContextAccessor accessor) : ICaller
{
    public string TenantId => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new HubException("unauthorized", 401);
    public string Role => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Role) ?? "";
}
public interface ISecretProtector { string Protect(string value); string Unprotect(string value); }
public sealed class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("NotificationHub.Credentials.v1");
    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}
public sealed class HubOptions
{
    public bool RunWorkers { get; set; } = true;
    public bool MigrateOnStartup { get; set; } = true;
    public int WorkerConcurrency { get; set; } = 4;
    public int LeaseSeconds { get; set; } = 60;
    public int MaxPendingPerTenant { get; set; } = 10000;
    public int NewDeliveriesPerMinute { get; set; } = 600;
    public int IngressPerMinute { get; set; } = 300;
    public int TenantSendsPerMinute { get; set; } = 600;
    public int TenantConcurrency { get; set; } = 16;
    public int ConnectionConcurrency { get; set; } = 4;
    public int CircuitFailureThreshold { get; set; } = 5;
    public int CircuitCooldownSeconds { get; set; } = 60;
    public int HealthReminderMinutes { get; set; } = 60;
    public int OutboxMaxAttempts { get; set; } = 20;
    public string[] AllowedHosts { get; set; } = [];
    public bool AllowPrivateNetworks { get; set; }
    public string? TicketWebhookUrl { get; set; }
    public string? TicketWebhookSecret { get; set; }
}
public sealed class ApiClient { public string Id { get; set; } = ""; public string Key { get; set; } = ""; public string Role { get; set; } = "business"; }

public sealed class MessageCatalog
{
    private readonly Dictionary<string, Dictionary<string, string>> catalogs;
    public MessageCatalog(IWebHostEnvironment environment)
    {
        catalogs = new[] { "en", "zh-CN" }.ToDictionary(lang => lang,
            lang => JsonDefaults.Read<Dictionary<string, string>>(File.ReadAllText(
                Path.Combine(environment.ContentRootPath, "Resources", $"messages.{lang}.json"))));
    }
    public string Get(string code)
    {
        var lang = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
        return catalogs[lang].GetValueOrDefault(code) ?? catalogs["en"].GetValueOrDefault(code) ?? code;
    }
}
