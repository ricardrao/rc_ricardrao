using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NotificationHub.Api.Models;
using NotificationHub.Api.Services;

namespace NotificationHub.Api.Controllers;

[ApiController, Route("v1/connections"), Authorize(Roles = "business,admin")]
public sealed class ConnectionsController(IConnectionService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.List(ct));
    [HttpPost]
    public async Task<IActionResult> Create(ConnectRequest request, CancellationToken ct)
    { var result = await service.Create(request, ct); return Created($"/v1/connections/{result.Id}/health", result); }
    [HttpPatch("{id:guid}/enabled")]
    public async Task<IActionResult> SetEnabled(Guid id, EnabledRequest request, CancellationToken ct)
    { await service.SetEnabled(id, request.Enabled, ct); return NoContent(); }
    [HttpPut("{id:guid}/credential")]
    public async Task<IActionResult> RotateCredential(Guid id, CredentialRequest request, CancellationToken ct)
    { await service.RotateCredential(id, request, ct); return NoContent(); }
    [HttpGet("{id:guid}/health")]
    public async Task<IActionResult> Health(Guid id, CancellationToken ct) => Ok(await service.Health(id, ct));
}
