using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NotificationHub.Api.Models;
using NotificationHub.Api.Services;

namespace NotificationHub.Api.Controllers;

[ApiController, Route("v1"), Authorize(Roles = "business,admin")]
public sealed class NotificationsController(INotificationService service) : ControllerBase
{
    [HttpGet("notifications")]
    public async Task<IActionResult> List(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? status = null)
        => Ok(await service.List(page, pageSize, status, ct));
    [HttpPost("notifications")]
    public async Task<IActionResult> Submit([FromHeader(Name = "Idempotency-Key")] string key, SubmitNotificationRequest request, CancellationToken ct)
    {
        var result = await service.Submit(key, request, ct);
        return result.Duplicate ? Ok(result) : Accepted($"/v1/notifications/{result.Id}", result);
    }
    [HttpGet("notifications/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await service.Get(id, ct));
    [HttpGet("deliveries/{id:guid}/attempts")]
    public async Task<IActionResult> Attempts(Guid id, CancellationToken ct) => Ok(await service.Attempts(id, ct));
    [HttpPost("deliveries/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, ActionRequest request, CancellationToken ct)
    { await service.Retry(id, request, ct); return NoContent(); }
    [HttpPost("deliveries/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, ActionRequest request, CancellationToken ct)
    { await service.Cancel(id, request, ct); return NoContent(); }
    [HttpPost("deliveries/{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, ActionRequest request, CancellationToken ct)
    { await service.Confirm(id, request, ct); return NoContent(); }
}
