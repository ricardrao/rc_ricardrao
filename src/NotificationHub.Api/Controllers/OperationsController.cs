using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NotificationHub.Api.Models;
using NotificationHub.Api.Services;

namespace NotificationHub.Api.Controllers;

[ApiController, Route("v1"), Authorize(Roles = "business,admin")]
public sealed class OperationsController(IOperationsService service) : ControllerBase
{
    [HttpGet("tickets")]
    public async Task<IActionResult> Tickets(CancellationToken ct) => Ok(await service.Tickets(ct));
    [HttpGet("tickets/{id:guid}")]
    public async Task<IActionResult> Ticket(Guid id, CancellationToken ct) => Ok(await service.Ticket(id, ct));
    [HttpPost("tickets/{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, ResolveTicketRequest request, CancellationToken ct)
    { await service.Resolve(id, request, ct); return NoContent(); }
    [HttpGet("subscriptions")]
    public async Task<IActionResult> Subscriptions(CancellationToken ct) => Ok(await service.Subscriptions(ct));
    [HttpPost("subscriptions")]
    public async Task<IActionResult> Subscribe(SubscribeRequest request, CancellationToken ct)
    { var result = await service.Subscribe(request, ct); return Created("/v1/subscriptions", result); }
    [HttpDelete("subscriptions/{id:guid}")]
    public async Task<IActionResult> Unsubscribe(Guid id, CancellationToken ct)
    { await service.Unsubscribe(id, ct); return NoContent(); }
    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics(CancellationToken ct) => Ok(await service.Metrics(ct));
    [HttpGet("outbox")]
    public async Task<IActionResult> Outbox(CancellationToken ct) => Ok(await service.Outbox(ct));
    [HttpPost("outbox/{id:guid}/retry")]
    public async Task<IActionResult> RetryOutbox(Guid id, ActionRequest request, CancellationToken ct)
    { await service.RetryOutbox(id, request, ct); return NoContent(); }
}
