using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Models;

namespace NotificationHub.Api.Services;

public interface IProtocolAdapter
{
    void Validate(OperationDefinition definition);
    PreparedRequest Build(OperationDefinition definition, JsonElement data, Guid deliveryId, string? credential);
    DeliveryOutcome Classify(OperationDefinition definition, int status, string body, int? retryAfter = null);
}
public sealed partial class ProtocolAdapter : IProtocolAdapter
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,100}$")]
    private static partial Regex Identifier();
    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderName();
    public void Validate(OperationDefinition d)
    {
        if (d is null || string.IsNullOrWhiteSpace(d.EventType) || !Identifier().IsMatch(d.EventType)
            || d.Method is not ("POST" or "PUT" or "PATCH") || d.Encoding is not ("json" or "form")
            || d.AuthType is not ("none" or "bearer" or "header")
            || d.TimeoutSeconds is < 1 or > 30 || d.MaxAttempts is < 1 or > 20
            || d.MaxAgeHours is < 1 or > 168 || d.IdempotencyRetentionHours < d.MaxAgeHours
            || d.IdempotencyRetentionHours > 8760 || d.RequestsPerMinute is < 1 or > 10000
            || d.Description?.Length > 2000
            || d.Inputs is null || d.Body is null || d.Headers is null || d.Success is null
            || d.Success.HttpStatuses is not { Length: > 0 } || d.Success.HttpStatuses.Any(x => x is < 200 or > 299)
            || d.Inputs.Count > 100 || d.Body.Count > 100 || d.Headers.Count > 30)
            throw new HubException("invalid_mapping");
        if (d.Inputs.Any(x => x is null) || d.Inputs.Select(x => x.Name).Distinct().Count() != d.Inputs.Count
            || d.Inputs.Any(x => x is null || string.IsNullOrWhiteSpace(x.Name) || !Identifier().IsMatch(x.Name) || x.Type is not ("string" or "number" or "boolean" or "object" or "array"))
            || d.Body.Keys.Any(x => !Identifier().IsMatch(x))
            || d.Body.Values.Any(x => x is null || (x.Source is null) == (x.Constant is null)
                || (x.Source is not null && (!x.Source.StartsWith("data.", StringComparison.Ordinal) || !Identifier().IsMatch(x.Source))))
            || (d.Success.BodyPath is not null && (!Identifier().IsMatch(d.Success.BodyPath) || d.Success.ExpectedValue is null))
            || (d.RetryableStatuses?.Any(x => x is < 400 or > 599) ?? false))
            throw new HubException("invalid_mapping");
        var names = d.Headers.Keys.Append(d.AuthHeader).Append(d.IdempotencyHeader);
        if (names.Any(x => string.IsNullOrEmpty(x) || !HeaderName().IsMatch(x) || ForbiddenHeader(x))
            || d.Headers.Any(x => x.Value is null || x.Value.Contains('\r') || x.Value.Contains('\n'))
            || d.Headers.Keys.Any(x => x.Equals(d.AuthHeader, StringComparison.OrdinalIgnoreCase)
                || x.Equals(d.IdempotencyHeader, StringComparison.OrdinalIgnoreCase))
            || d.AuthHeader.Equals(d.IdempotencyHeader, StringComparison.OrdinalIgnoreCase))
            throw new HubException("invalid_mapping");
    }
    private static bool ForbiddenHeader(string name) => new[] { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Content-Type", "Cookie", "Proxy-Authorization" }
        .Contains(name, StringComparer.OrdinalIgnoreCase);
    public PreparedRequest Build(OperationDefinition d, JsonElement data, Guid deliveryId, string? credential)
    {
        Validate(d);
        if (data.ValueKind != JsonValueKind.Object) throw new HubException("invalid_mapping");
        foreach (var field in d.Inputs)
        {
            var value = ReadPath(data, field.Name);
            if (value is null) { if (field.Required) throw new HubException("invalid_mapping"); continue; }
            var valid = field.Type switch
            {
                "string" => value.Value.ValueKind == JsonValueKind.String,
                "number" => value.Value.ValueKind == JsonValueKind.Number,
                "boolean" => value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "object" => value.Value.ValueKind == JsonValueKind.Object,
                "array" => value.Value.ValueKind == JsonValueKind.Array,
                _ => false
            };
            if (!valid) throw new HubException("invalid_mapping");
        }
        var mapped = new Dictionary<string, JsonElement>();
        foreach (var (key, rule) in d.Body)
        {
            var value = rule.Source is null ? rule.Constant : ReadPath(data, rule.Source[5..]);
            if (value is null) throw new HubException("invalid_mapping");
            if (rule.Values is not null)
            {
                if (!rule.Values.TryGetValue(Scalar(value.Value), out var translated)) throw new HubException("invalid_mapping");
                value = translated;
            }
            mapped[key] = value.Value;
        }
        string body;
        if (d.Encoding == "form") body = string.Join("&", mapped.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(Scalar(x.Value))}"));
        else
        {
            var root = new JsonObject();
            foreach (var (key, value) in mapped)
            {
                var parts = key.Split('.'); var current = root;
                foreach (var part in parts[..^1])
                {
                    if (!current.TryGetPropertyValue(part, out var child)) current[part] = child = new JsonObject();
                    current = child as JsonObject ?? throw new HubException("invalid_mapping");
                }
                if (current.ContainsKey(parts[^1])) throw new HubException("invalid_mapping");
                current[parts[^1]] = JsonNode.Parse(value.GetRawText());
            }
            body = root.ToJsonString();
        }
        var headers = new Dictionary<string, string>(d.Headers, StringComparer.OrdinalIgnoreCase) { [d.IdempotencyHeader] = deliveryId.ToString() };
        if (d.AuthType != "none")
        {
            if (string.IsNullOrWhiteSpace(credential) || credential.Contains('\r') || credential.Contains('\n')) throw new HubException("invalid_mapping");
            headers[d.AuthHeader] = d.AuthType == "bearer" ? $"Bearer {credential}" : credential;
        }
        return new(d.Url, d.Method, headers, body, d.Encoding == "json" ? "application/json" : "application/x-www-form-urlencoded");
    }
    public DeliveryOutcome Classify(OperationDefinition d, int status, string body, int? retryAfter = null)
    {
        if (d.Success.HttpStatuses.Contains(status))
        {
            if (d.Success.BodyPath is null) return new(true, false, "accepted", status);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var actual = ReadPath(doc.RootElement, d.Success.BodyPath);
                if (actual is not null && JsonElement.DeepEquals(actual.Value, d.Success.ExpectedValue!.Value))
                    return new(true, false, "accepted", status);
                return new(false, false, "business_rejected", status);
            }
            catch (JsonException) { return new(false, true, "invalid_response", status, true); }
        }
        var retryable = d.RetryableStatuses?.Contains(status) ?? (status is 408 or 429 or >= 500);
        var category = status switch { 401 or 403 => "authentication", 429 => "rate_limited", >= 500 => "upstream_error", _ => "http_rejected" };
        return new(false, retryable, category, status, status == 408, retryAfter);
    }
    public static JsonElement? ReadPath(JsonElement node, string path)
    {
        foreach (var part in path.Split('.'))
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node)) return null;
        return node;
    }
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null => "",
        _ => throw new HubException("invalid_mapping")
    };
}

public interface IHttpDelivery { Task<DeliveryOutcome> Send(PreparedRequest request, OperationDefinition definition, DateTimeOffset expiresAt, CancellationToken ct); }
public sealed class HttpDelivery(IHttpClientFactory clients, IUrlPolicy urls, IProtocolAdapter adapter, TimeProvider time) : IHttpDelivery
{
    public async Task<DeliveryOutcome> Send(PreparedRequest request, OperationDefinition definition, DateTimeOffset expiresAt, CancellationToken ct)
    {
        if (expiresAt <= time.GetUtcNow()) return new(false, false, "expired");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, Math.Min(definition.TimeoutSeconds, (expiresAt - time.GetUtcNow()).TotalSeconds))));
        try
        {
            await urls.Validate(request.Url, timeout.Token);
            if (expiresAt <= time.GetUtcNow()) return new(false, false, "expired");
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url)
            { Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType) };
            foreach (var header in request.Headers) message.Headers.Add(header.Key, header.Value);
            using var response = await clients.CreateClient("delivery").SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[65537]; var length = 0;
            while (length < buffer.Length)
            { var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token); if (read == 0) break; length += read; }
            if (length > 65536) return new(false, false, "response_too_large", (int)response.StatusCode, true);
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta?.TotalSeconds ?? (retry?.Date - time.GetUtcNow())?.TotalSeconds;
            return adapter.Classify(definition, (int)response.StatusCode, Encoding.UTF8.GetString(buffer, 0, length),
                delay is null ? null : (int)Math.Clamp(Math.Ceiling(delay.Value), 1, 86400));
        }
        catch (HubException) { return new(false, false, "unsafe_destination"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, true, "timeout", Uncertain: true); }
        catch (HttpRequestException) { return new(false, true, "network_error", Uncertain: true); }
        catch (System.Net.Sockets.SocketException) { return new(false, true, "network_error"); }
    }
}
public static class RetryPolicy
{
    public static TimeSpan Delay(int attempt, int? retryAfterSeconds = null, double? jitter = null)
    {
        var seconds = Math.Min(3600, 10 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20)));
        var delayed = seconds * (0.5 + 0.5 * (jitter ?? Random.Shared.NextDouble()));
        return TimeSpan.FromSeconds(Math.Max(delayed, retryAfterSeconds ?? 0));
    }
}
