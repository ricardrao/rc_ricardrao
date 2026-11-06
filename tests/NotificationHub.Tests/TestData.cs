using System.Text.Json;
using NotificationHub.Api.Models;

namespace NotificationHub.Tests;

public static class TestData
{
    public static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OtherId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    public static OperationDefinition Definition(string url = "https://example.com/callback") => new(
        "contact.updated.v1", url, "POST", "json", new(),
        new() { ["contact.id"] = new("data.contact_id"), ["state"] = new("data.status", Values: new() { ["paid"] = Json("1") }) },
        [new("contact_id"), new("status")], new([200], "code", Json("0")));
    public static SubmitNotificationRequest Request(Guid? connection = null) => new("contact.updated.v1",
        [new(connection ?? Id, "contact.update")], Json("{\"contact_id\":\"c1\",\"status\":\"paid\"}"));
    public static readonly ActionRequest Reason = new("Investigated and corrected the issue.");
}
