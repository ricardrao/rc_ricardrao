using Microsoft.AspNetCore.Mvc;
using Moq;
using NotificationHub.Api.Controllers;
using NotificationHub.Api.Models;
using NotificationHub.Api.Services;

namespace NotificationHub.Tests;

public sealed class ProviderControllerTests
{
    [Theory]
    [InlineData("List", 200)] [InlineData("Register", 201)] [InlineData("Versions", 200)]
    [InlineData("AddVersion", 201)] [InlineData("Preview", 200)] [InlineData("Publish", 204)] [InlineData("SetEnabled", 204)]
    public async Task Each_endpoint_delegates_and_returns_expected_status(string action, int status)
    {
        var service = new Mock<IProviderService>(MockBehavior.Strict);
        var ct = new CancellationTokenSource().Token;
        var provider = new ProviderRow { Id = TestData.Id };
        var version = new VersionRow { Id = TestData.OtherId };
        var register = new RegisterProviderRequest("CRM");
        var definition = TestData.Definition();
        var preview = new PreviewRequest(TestData.Request().Data);
        var controller = new ProvidersController(service.Object);
        IActionResult result;
        switch (action)
        {
            case "List": service.Setup(x => x.List(ct)).ReturnsAsync(new[] { provider }); result = await controller.List(ct); break;
            case "Register": service.Setup(x => x.Register(register, ct)).ReturnsAsync(provider); result = await controller.Register(register, ct); break;
            case "Versions": service.Setup(x => x.Versions(TestData.Id, ct)).ReturnsAsync(new[] { version }); result = await controller.Versions(TestData.Id, ct); break;
            case "AddVersion": service.Setup(x => x.AddVersion(TestData.Id, "update", definition, ct)).ReturnsAsync(version); result = await controller.AddVersion(TestData.Id, "update", definition, ct); break;
            case "Preview": service.Setup(x => x.Preview(TestData.Id, TestData.OtherId, preview, ct)).ReturnsAsync(new PreparedRequest("https://example.com", "POST", new(), "{}", "application/json")); result = await controller.Preview(TestData.Id, TestData.OtherId, preview, ct); break;
            case "Publish": service.Setup(x => x.Publish(TestData.Id, TestData.OtherId, ct)).Returns(Task.CompletedTask); result = await controller.Publish(TestData.Id, TestData.OtherId, ct); break;
            default: service.Setup(x => x.SetEnabled(TestData.Id, false, ct)).Returns(Task.CompletedTask); result = await controller.SetEnabled(TestData.Id, new(false), ct); break;
        }
        Assert.Equal(status, result is ObjectResult o ? o.StatusCode : ((StatusCodeResult)result).StatusCode);
        service.VerifyAll(); service.VerifyNoOtherCalls();
    }
}
public sealed class ConnectionControllerTests
{
    [Theory]
    [InlineData("List", 200)] [InlineData("Create", 201)] [InlineData("SetEnabled", 204)] [InlineData("RotateCredential", 204)] [InlineData("Health", 200)]
    public async Task Each_endpoint_delegates_and_returns_expected_status(string action, int status)
    {
        var service = new Mock<IConnectionService>(MockBehavior.Strict);
        var controller = new ConnectionsController(service.Object);
        var ct = new CancellationTokenSource().Token;
        IActionResult result;
        switch (action)
        {
            case "List": service.Setup(x => x.List(ct)).ReturnsAsync(Array.Empty<ConnectionRow>()); result = await controller.List(ct); break;
            case "Create":
                var request = new ConnectRequest(TestData.Id, "CRM", "token");
                service.Setup(x => x.Create(request, ct)).ReturnsAsync(new ConnectionRow { Id = TestData.OtherId }); result = await controller.Create(request, ct); break;
            case "SetEnabled": service.Setup(x => x.SetEnabled(TestData.Id, false, ct)).Returns(Task.CompletedTask); result = await controller.SetEnabled(TestData.Id, new(false), ct); break;
            case "RotateCredential":
                var credential = new CredentialRequest("token"); service.Setup(x => x.RotateCredential(TestData.Id, credential, ct)).Returns(Task.CompletedTask); result = await controller.RotateCredential(TestData.Id, credential, ct); break;
            default: service.Setup(x => x.Health(TestData.Id, ct)).ReturnsAsync(Array.Empty<HealthRow>()); result = await controller.Health(TestData.Id, ct); break;
        }
        Assert.Equal(status, result is ObjectResult o ? o.StatusCode : ((StatusCodeResult)result).StatusCode);
        service.VerifyAll(); service.VerifyNoOtherCalls();
    }
}
public sealed class NotificationControllerTests
{
    [Fact]
    public async Task List_forwards_pagination_and_status_filter()
    {
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var page = new NotificationPage([], 2, 10, 0);
        service.Setup(x => x.List(2, 10, "stopped", default)).ReturnsAsync(page);
        var result = Assert.IsType<OkObjectResult>(await new NotificationsController(service.Object).List(default, 2, 10, "stopped"));
        Assert.Same(page, result.Value); service.VerifyAll(); service.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData(false, 202)] [InlineData(true, 200)]
    public async Task Submit_distinguishes_new_from_duplicate(bool duplicate, int status)
    {
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var request = TestData.Request(); var ct = new CancellationTokenSource().Token;
        var receipt = new NotificationReceipt(TestData.Id, "accepted", duplicate, []);
        service.Setup(x => x.Submit("event-1", request, ct)).ReturnsAsync(receipt);
        var result = Assert.IsAssignableFrom<ObjectResult>(await new NotificationsController(service.Object).Submit("event-1", request, ct));
        Assert.Equal(status, result.StatusCode); Assert.Same(receipt, result.Value);
        service.VerifyAll(); service.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("Get", 200)] [InlineData("Attempts", 200)] [InlineData("Retry", 204)] [InlineData("Cancel", 204)] [InlineData("Confirm", 204)]
    public async Task Each_endpoint_delegates_and_returns_expected_status(string action, int status)
    {
        var service = new Mock<INotificationService>(MockBehavior.Strict);
        var controller = new NotificationsController(service.Object); var ct = new CancellationTokenSource().Token;
        IActionResult result;
        switch (action)
        {
            case "Get": service.Setup(x => x.Get(TestData.Id, ct)).ReturnsAsync(new NotificationView(new(), [])); result = await controller.Get(TestData.Id, ct); break;
            case "Attempts": service.Setup(x => x.Attempts(TestData.Id, ct)).ReturnsAsync(Array.Empty<AttemptRow>()); result = await controller.Attempts(TestData.Id, ct); break;
            case "Retry": service.Setup(x => x.Retry(TestData.Id, TestData.Reason, ct)).Returns(Task.CompletedTask); result = await controller.Retry(TestData.Id, TestData.Reason, ct); break;
            case "Cancel": service.Setup(x => x.Cancel(TestData.Id, TestData.Reason, ct)).Returns(Task.CompletedTask); result = await controller.Cancel(TestData.Id, TestData.Reason, ct); break;
            default: service.Setup(x => x.Confirm(TestData.Id, TestData.Reason, ct)).Returns(Task.CompletedTask); result = await controller.Confirm(TestData.Id, TestData.Reason, ct); break;
        }
        Assert.Equal(status, result is ObjectResult o ? o.StatusCode : ((StatusCodeResult)result).StatusCode);
        service.VerifyAll(); service.VerifyNoOtherCalls();
    }
}
public sealed class OperationsControllerTests
{
    [Theory]
    [InlineData("Tickets", 200)] [InlineData("Ticket", 200)] [InlineData("Resolve", 204)]
    [InlineData("Subscriptions", 200)] [InlineData("Subscribe", 201)] [InlineData("Unsubscribe", 204)]
    [InlineData("Metrics", 200)] [InlineData("Outbox", 200)] [InlineData("RetryOutbox", 204)]
    public async Task Each_endpoint_delegates_and_returns_expected_status(string action, int status)
    {
        var service = new Mock<IOperationsService>(MockBehavior.Strict);
        var controller = new OperationsController(service.Object); var ct = new CancellationTokenSource().Token;
        IActionResult result;
        switch (action)
        {
            case "Tickets": service.Setup(x => x.Tickets(ct)).ReturnsAsync(Array.Empty<TicketRow>()); result = await controller.Tickets(ct); break;
            case "Ticket": service.Setup(x => x.Ticket(TestData.Id, ct)).ReturnsAsync(new { id = TestData.Id }); result = await controller.Ticket(TestData.Id, ct); break;
            case "Resolve":
                var resolution = new ResolveTicketRequest("fixed", "Credential rotated."); service.Setup(x => x.Resolve(TestData.Id, resolution, ct)).Returns(Task.CompletedTask); result = await controller.Resolve(TestData.Id, resolution, ct); break;
            case "Subscriptions": service.Setup(x => x.Subscriptions(ct)).ReturnsAsync(Array.Empty<SubscriptionRow>()); result = await controller.Subscriptions(ct); break;
            case "Subscribe":
                var request = new SubscribeRequest("https://example.com/hook", new string('x', 32)); service.Setup(x => x.Subscribe(request, ct)).ReturnsAsync(new SubscriptionRow { Id = TestData.Id }); result = await controller.Subscribe(request, ct); break;
            case "Unsubscribe": service.Setup(x => x.Unsubscribe(TestData.Id, ct)).Returns(Task.CompletedTask); result = await controller.Unsubscribe(TestData.Id, ct); break;
            case "Metrics": service.Setup(x => x.Metrics(ct)).ReturnsAsync(new { pending = 1 }); result = await controller.Metrics(ct); break;
            case "Outbox": service.Setup(x => x.Outbox(ct)).ReturnsAsync(new { failed = 1 }); result = await controller.Outbox(ct); break;
            default: service.Setup(x => x.RetryOutbox(TestData.Id, TestData.Reason, ct)).Returns(Task.CompletedTask); result = await controller.RetryOutbox(TestData.Id, TestData.Reason, ct); break;
        }
        Assert.Equal(status, result is ObjectResult o ? o.StatusCode : ((StatusCodeResult)result).StatusCode);
        service.VerifyAll(); service.VerifyNoOtherCalls();
    }
}
