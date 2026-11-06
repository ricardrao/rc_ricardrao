using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Services;

namespace NotificationHub.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Description_is_bounded_metadata_and_does_not_change_supplier_payload()
    {
        var definition = TestData.Definition();
        var described = definition with { Description = new string('x', 2000) };
        Assert.Equal(adapter.Build(definition, TestData.Request().Data, TestData.Id, null).Body,
            adapter.Build(described, TestData.Request().Data, TestData.Id, null).Body);
        Assert.Throws<HubException>(() => adapter.Validate(definition with { Description = new string('x', 2001) }));
    }
    [Fact]
    public void Legacy_definition_without_description_remains_valid()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonDefaults.Serialize(TestData.Definition()))!.AsObject();
        node.Remove("description");
        var definition = JsonDefaults.Read<NotificationHub.Api.Models.OperationDefinition>(node.ToJsonString());
        Assert.Null(definition.Description); adapter.Validate(definition);
    }
    private readonly ProtocolAdapter adapter = new();
    [Fact]
    public void Json_mapping_preserves_types_and_nested_fields()
    {
        var result = adapter.Build(TestData.Definition(), TestData.Request().Data, TestData.Id, null);
        var body = TestData.Json(result.Body);
        Assert.Equal("c1", body.GetProperty("contact").GetProperty("id").GetString());
        Assert.Equal(1, body.GetProperty("state").GetInt32());
        Assert.Equal(TestData.Id.ToString(), result.Headers["Idempotency-Key"]);
    }
    [Fact]
    public void Form_mapping_encodes_values_and_injects_credentials()
    {
        var definition = TestData.Definition() with { Encoding = "form", AuthType = "header", AuthHeader = "X-Api-Key" };
        var result = adapter.Build(definition, TestData.Json("{\"contact_id\":\"a&b c\",\"status\":\"paid\"}"), TestData.Id, "private");
        Assert.Contains("a%26b%20c", result.Body); Assert.Equal("private", result.Headers["X-Api-Key"]);
        Assert.Equal("application/x-www-form-urlencoded", result.ContentType);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"contact_id\":7,\"status\":\"paid\"}")]
    [InlineData("{\"contact_id\":\"c\",\"status\":\"unknown\"}")]
    public void Missing_or_incompatible_inputs_are_rejected(string data) =>
        Assert.Equal("invalid_mapping", Assert.Throws<HubException>(() => adapter.Build(TestData.Definition(), TestData.Json(data), TestData.Id, null)).Code);
    [Theory]
    [InlineData("Host")] [InlineData("Content-Length")] [InlineData("Connection")] [InlineData("Content-Type")]
    public void Unsafe_headers_are_rejected(string header) => Assert.Throws<HubException>(() => adapter.Validate(TestData.Definition() with { Headers = new() { [header] = "value" } }));
    [Fact]
    public void Newline_header_injection_is_rejected() => Assert.Throws<HubException>(() => adapter.Validate(TestData.Definition() with { Headers = new() { ["X-Test"] = "x\r\ny" } }));
    [Fact]
    public void Dedup_retention_must_cover_notification_lifetime() => Assert.Throws<HubException>(() => adapter.Validate(TestData.Definition() with { IdempotencyRetentionHours = 1 }));
    [Theory]
    [InlineData(200, "{\"code\":0}", true, false)]
    [InlineData(200, "{\"code\":1}", false, false)]
    [InlineData(200, "invalid", false, true)]
    [InlineData(400, "{}", false, false)]
    [InlineData(401, "{}", false, false)]
    [InlineData(429, "{}", false, true)]
    [InlineData(503, "{}", false, true)]
    [InlineData(302, "{}", false, false)]
    public void Responses_use_supplier_success_contract(int status, string body, bool success, bool retryable)
    {
        var result = adapter.Classify(TestData.Definition(), status, body, 60);
        Assert.Equal(success, result.Success); Assert.Equal(retryable, result.Retryable);
    }
    [Fact]
    public void Retry_after_is_a_lower_bound_and_exponential_delay_is_capped()
    {
        Assert.Equal(120, RetryPolicy.Delay(1, 120, 0).TotalSeconds);
        Assert.Equal(3600, RetryPolicy.Delay(20, jitter: 1).TotalSeconds);
        Assert.Equal(5, RetryPolicy.Delay(1, jitter: 0).TotalSeconds);
    }
    [Theory]
    [InlineData("127.0.0.1")] [InlineData("10.0.0.1")] [InlineData("169.254.169.254")]
    [InlineData("192.168.1.1")] [InlineData("::1")] [InlineData("::ffff:127.0.0.1")] [InlineData("fc00::1")]
    public void Private_network_addresses_are_denied(string address) => Assert.True(UrlPolicy.IsPrivate(IPAddress.Parse(address)));
    [Fact]
    public async Task Unapproved_hosts_are_rejected_before_dns()
    {
        var policy = new UrlPolicy(Options.Create(new HubOptions()));
        Assert.Equal("unsafe_url", (await Assert.ThrowsAsync<HubException>(() => policy.Validate("https://example.com"))).Code);
    }
    [Fact]
    public void Canonical_hash_ignores_object_property_order()
    {
        Assert.Equal(JsonDefaults.Fingerprint(TestData.Json("{\"a\":1,\"b\":2}")), JsonDefaults.Fingerprint(TestData.Json("{\"b\":2,\"a\":1}")));
        Assert.NotEqual(JsonDefaults.Fingerprint(TestData.Json("{\"a\":1}")), JsonDefaults.Fingerprint(TestData.Json("{\"a\":2}")));
    }
    [Fact]
    public void Null_input_definition_is_a_validation_error() => Assert.Throws<HubException>(() => adapter.Validate(TestData.Definition() with { Inputs = [null!] }));
    [Fact]
    public async Task Null_target_is_rejected_before_database_access()
    {
        var service = new NotificationService(null!, new FakeCaller("tenant"), adapter, new TestSecrets(), Options.Create(new HubOptions()));
        var request = TestData.Request() with { Targets = [null!] };
        Assert.Equal("invalid_request", (await Assert.ThrowsAsync<HubException>(() => service.Submit("event", request, default))).Code);
    }
}
