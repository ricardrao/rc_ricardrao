using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NotificationHub.Api.Commons;
using NotificationHub.Api.Controllers;
using NotificationHub.Api.Models;
using NotificationHub.Api.Services;

namespace NotificationHub.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public Mock<INotificationService> Notifications { get; } = new();
    public Mock<ISharedLimiter> Limiter { get; } = new();
    public Mock<IHealthService> Health { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hub:RunWorkers"] = "false", ["Hub:MigrateOnStartup"] = "false",
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Database=unused;Username=unused",
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false",
            ["Clients:0:Id"] = "tenant-a", ["Clients:0:Key"] = "test-business-key-123456", ["Clients:0:Role"] = "business",
            ["Clients:1:Id"] = "supplier-a", ["Clients:1:Key"] = "test-provider-key-123456", ["Clients:1:Role"] = "provider"
        }));
        builder.ConfigureServices(services =>
        {
            Limiter.Setup(x => x.Take(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(true);
            services.RemoveAll<ISharedLimiter>(); services.AddSingleton(Limiter.Object);
            services.RemoveAll<INotificationService>(); services.AddSingleton(Notifications.Object);
            services.RemoveAll<IHealthService>(); services.AddSingleton(Health.Object);
        });
    }
}
public sealed class HttpApiTests
{
    [Fact]
    public async Task Capability_description_over_limit_returns_validation_error()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-provider-key-123456");
        using var response = await client.PostAsJsonAsync($"/v1/providers/{TestData.Id}/operations/contact.update/versions",
            TestData.Definition() with { Description = new string('x', 2001) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync());
    }
    public static IEnumerable<object[]> ProtectedRoutes()
    {
        foreach (var controller in new[] { typeof(ProvidersController), typeof(ConnectionsController), typeof(NotificationsController), typeof(OperationsController) })
        {
            var prefix = controller.GetCustomAttribute<RouteAttribute>()!.Template;
            foreach (var method in controller.GetMethods())
                foreach (var route in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    var path = $"/{prefix}/{route.Template}".TrimEnd('/');
                    path = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]+:guid\}", TestData.Id.ToString());
                    path = path.Replace("{operation}", "contact.update", StringComparison.Ordinal);
                    yield return [route.HttpMethods.Single(), path];
                }
        }
    }
    [Theory, MemberData(nameof(ProtectedRoutes))]
    public async Task Every_business_endpoint_requires_authentication(string method, string path)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Fact]
    public async Task Business_client_cannot_publish_provider_configuration()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-business-key-123456");
        using var response = await client.PostAsJsonAsync("/v1/providers", new RegisterProviderRequest("CRM"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    [Fact]
    public async Task Language_can_change_per_request_without_restart()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        factory.Notifications.Setup(x => x.Get(TestData.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new HubException("not_found", 404));
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-business-key-123456");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN");
        using var chinese = await client.GetAsync($"/v1/notifications/{TestData.Id}");
        var first = TestData.Json(await chinese.Content.ReadAsStringAsync());
        Assert.Equal("\u8d44\u6e90\u4e0d\u5b58\u5728\u3002", first.GetProperty("message").GetString());
        using var english = await client.GetAsync($"/v1/notifications/{TestData.Id}?culture=en");
        var second = TestData.Json(await english.Content.ReadAsStringAsync());
        Assert.Equal("The resource was not found.", second.GetProperty("message").GetString());
        Assert.Equal(first.GetProperty("code").GetString(), second.GetProperty("code").GetString());
    }
    [Fact]
    public async Task Malformed_input_returns_localized_validation_error_without_calling_service()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-business-key-123456");
        using var response = await client.PostAsJsonAsync("/v1/notifications", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync());
        factory.Notifications.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task Throttled_request_does_not_reach_service_and_returns_retry_after()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        factory.Limiter.Setup(x => x.Take(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(false);
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-business-key-123456");
        using var response = await client.GetAsync($"/v1/notifications/{TestData.Id}");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        factory.Notifications.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task Submission_preserves_header_and_returns_location()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        factory.Notifications.Setup(x => x.Submit("event-123", It.IsAny<SubmitNotificationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationReceipt(TestData.Id, "accepted", false, []));
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-business-key-123456");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "event-123");
        using var response = await client.PostAsJsonAsync("/v1/notifications", TestData.Request());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/v1/notifications/{TestData.Id}", response.Headers.Location!.OriginalString);
    }
    [Fact]
    public async Task Liveness_endpoint_is_public()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
    [Theory]
    [InlineData(true, 200)] [InlineData(false, 503)]
    public async Task Readiness_exposes_dependency_availability(bool ready, int status)
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        factory.Health.Setup(x => x.Ready(It.IsAny<CancellationToken>())).ReturnsAsync(ready);
        Assert.Equal(status, (int)(await client.GetAsync("/health/ready")).StatusCode);
    }
}
