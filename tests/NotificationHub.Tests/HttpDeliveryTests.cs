using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NotificationHub.Api.Controllers;
using NotificationHub.Api.Models;
using NotificationHub.Api.Services;

namespace NotificationHub.Tests;

public sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
public sealed class StubClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
public sealed class HttpDeliveryTests
{
    [Fact]
    public async Task Actual_http_request_contains_mapping_authentication_and_stable_key()
    {
        using var client = new HttpClient(new StubHandler(async (message, ct) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("Bearer secret", message.Headers.Authorization!.ToString());
            Assert.Equal(TestData.Id.ToString(), Assert.Single(message.Headers.GetValues("Idempotency-Key")));
            Assert.Contains("\"state\":1", await message.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0}") };
        }));
        var adapter = new ProtocolAdapter(); var definition = TestData.Definition() with { AuthType = "bearer" };
        var sender = new HttpDelivery(new StubClientFactory(client), new TestUrls(), adapter, TimeProvider.System);
        var result = await sender.Send(adapter.Build(definition, TestData.Request().Data, TestData.Id, "secret"), definition, DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.True(result.Success);
    }
    [Fact]
    public async Task Expired_task_never_sends_http_request()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        var adapter = new ProtocolAdapter(); var definition = TestData.Definition();
        var sender = new HttpDelivery(new StubClientFactory(client), new TestUrls(), adapter, TimeProvider.System);
        var result = await sender.Send(adapter.Build(definition, TestData.Request().Data, TestData.Id, null), definition, DateTimeOffset.UtcNow.AddSeconds(-1), default);
        Assert.Equal("expired", result.Category); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task Network_failure_is_marked_uncertain_and_retryable()
    {
        using var client = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("Connection reset.")));
        var adapter = new ProtocolAdapter(); var definition = TestData.Definition();
        var sender = new HttpDelivery(new StubClientFactory(client), new TestUrls(), adapter, TimeProvider.System);
        var result = await sender.Send(adapter.Build(definition, TestData.Request().Data, TestData.Id, null), definition, DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.True(result.Retryable); Assert.True(result.Uncertain);
    }
    [Fact]
    public async Task Retry_after_header_is_preserved()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        }));
        var adapter = new ProtocolAdapter(); var definition = TestData.Definition();
        var sender = new HttpDelivery(new StubClientFactory(client), new TestUrls(), adapter, TimeProvider.System);
        var result = await sender.Send(adapter.Build(definition, TestData.Request().Data, TestData.Id, null), definition, DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.Equal(90, result.RetryAfterSeconds);
    }
    [Fact]
    public async Task Large_responses_are_bounded()
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 70000), Encoding.UTF8) })));
        var adapter = new ProtocolAdapter(); var definition = TestData.Definition();
        var sender = new HttpDelivery(new StubClientFactory(client), new TestUrls(), adapter, TimeProvider.System);
        var result = await sender.Send(adapter.Build(definition, TestData.Request().Data, TestData.Id, null), definition, DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.Equal("response_too_large", result.Category); Assert.True(result.Uncertain);
    }
    [Fact]
    public void Health_controller_liveness_never_calls_dependencies()
    {
        var service = new Mock<IHealthService>(MockBehavior.Strict);
        Assert.IsType<OkObjectResult>(new HealthController(service.Object).Live());
        service.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData(true, 200)] [InlineData(false, 503)]
    public async Task Health_controller_readiness_reports_service_result(bool ready, int status)
    {
        var service = new Mock<IHealthService>(MockBehavior.Strict);
        service.Setup(x => x.Ready(default)).ReturnsAsync(ready);
        var result = await new HealthController(service.Object).Ready(default);
        Assert.Equal(status, result is ObjectResult o ? o.StatusCode : ((StatusCodeResult)result).StatusCode);
        service.VerifyAll(); service.VerifyNoOtherCalls();
    }
}
