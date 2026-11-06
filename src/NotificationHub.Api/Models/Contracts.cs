using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace NotificationHub.Api.Models;

public sealed record RegisterProviderRequest([Required, StringLength(100, MinimumLength = 1)] string Name);
public sealed record InputField(string Name, string Type = "string", bool Required = true);
public sealed record FieldMapping(string? Source = null, JsonElement? Constant = null,
    Dictionary<string, JsonElement>? Values = null);
public sealed record SuccessRule(int[] HttpStatuses, string? BodyPath = null, JsonElement? ExpectedValue = null);
public sealed record OperationDefinition(
    string EventType, string Url, string Method, string Encoding,
    Dictionary<string, string> Headers, Dictionary<string, FieldMapping> Body,
    List<InputField> Inputs, SuccessRule Success,
    string AuthType = "none", string AuthHeader = "Authorization",
    string IdempotencyHeader = "Idempotency-Key", int IdempotencyRetentionHours = 168,
    int TimeoutSeconds = 15, int MaxAttempts = 8, int MaxAgeHours = 24,
    int RequestsPerMinute = 60, int[]? RetryableStatuses = null,
    [StringLength(2000)] string? Description = null);
public sealed record PreviewRequest(JsonElement Data);
public sealed record ConnectRequest(Guid ProviderId, [Required] string Name, string? Credential);
public sealed record CredentialRequest([Required] string Credential);
public sealed record EnabledRequest(bool Enabled);
public sealed record DeliveryTarget(Guid ConnectionId, [Required] string Operation);
public sealed record SubmitNotificationRequest([Required] string EventType,
    [Required, MinLength(1), MaxLength(20)] List<DeliveryTarget> Targets,
    JsonElement Data, DateTimeOffset? ExpiresAt = null);
public sealed record ActionRequest([Required, StringLength(1000, MinimumLength = 3)] string Reason);
public sealed record ResolveTicketRequest([Required] string Resolution, [Required, StringLength(1000, MinimumLength = 3)] string Reason);
public sealed record SubscribeRequest([Required] string Url, [Required, MinLength(32)] string SigningSecret);
public sealed record DeliveryView(Guid Id, Guid ConnectionId, string Operation, string Status, string Health,
    DateTimeOffset ExpiresAt);
public sealed record NotificationReceipt(Guid Id, string Status, bool Duplicate, List<DeliveryView> Deliveries);
public sealed record NotificationView(NotificationRow Notification, IReadOnlyList<DeliveryRow> Deliveries);
public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, int Page, int PageSize, long Total);
public sealed record PreparedRequest(string Url, string Method, Dictionary<string, string> Headers,
    string Body, string ContentType);
public sealed record DeliveryOutcome(bool Success, bool Retryable, string Category, int? HttpStatus = null,
    bool Uncertain = false, int? RetryAfterSeconds = null);

public sealed class ProviderRow
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class VersionRow
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string Operation { get; set; } = "";
    public int Version { get; set; }
    public string Definition { get; set; } = "";
    public string Status { get; set; } = "draft";
}
public sealed class ConnectionRow
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid ProviderId { get; set; }
    public string Name { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Credential { get; set; }
    public bool Enabled { get; set; }
}
public sealed class HealthRow
{
    public Guid ConnectionId { get; set; }
    public string Operation { get; set; } = "";
    public string State { get; set; } = "healthy";
    public int ConsecutiveFailures { get; set; }
    public int RecoverySuccesses { get; set; }
    public DateTimeOffset? OpenUntil { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public int RecentAttempts { get; set; }
    public int RecentFailures { get; set; }
    public int PendingCount { get; set; }
    public double? FailureRate => RecentAttempts == 0 ? null : (double)RecentFailures / RecentAttempts;
}
public sealed class NotificationRow
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Data { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class DeliveryRow
{
    public Guid Id { get; set; }
    public Guid NotificationId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid ConnectionId { get; set; }
    public Guid VersionId { get; set; }
    public string Operation { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public int RoundAttempts { get; set; }
    public int RetryRound { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset DedupUntil { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? ClaimToken { get; set; }
    public string? LastError { get; set; }
    public bool Uncertain { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class AttemptRow
{
    public Guid Id { get; set; }
    public Guid DeliveryId { get; set; }
    public Guid ClaimToken { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Category { get; set; } = "in_flight";
    public int? HttpStatus { get; set; }
    public bool Uncertain { get; set; }
}
public sealed class TicketRow
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid ConnectionId { get; set; }
    public string Operation { get; set; } = "";
    public string Category { get; set; } = "";
    public string Status { get; set; } = "open";
    public int AffectedCount { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class OutboxRow
{
    public Guid Id { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "";
    public int Attempts { get; set; }
    public Guid ClaimToken { get; set; }
}
public sealed class SubscriptionRow
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Url { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string SigningSecret { get; set; } = "";
    public bool Enabled { get; set; }
}
