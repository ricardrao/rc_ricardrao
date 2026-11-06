using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NotificationHub.Api.Models;
using NotificationHub.Api.Services;

namespace NotificationHub.Api.Controllers;

[ApiController, Route("v1/providers"), Authorize]
public sealed class ProvidersController(IProviderService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.List(ct));
    [HttpPost, Authorize(Roles = "provider,admin")]
    public async Task<IActionResult> Register(RegisterProviderRequest request, CancellationToken ct)
    { var result = await service.Register(request, ct); return Created($"/v1/providers/{result.Id}/versions", result); }
    [HttpGet("{providerId:guid}/versions")]
    public async Task<IActionResult> Versions(Guid providerId, CancellationToken ct) => Ok(await service.Versions(providerId, ct));
    [HttpPost("{providerId:guid}/operations/{operation}/versions"), Authorize(Roles = "provider,admin")]
    public async Task<IActionResult> AddVersion(Guid providerId, string operation, OperationDefinition request, CancellationToken ct)
    { var result = await service.AddVersion(providerId, operation, request, ct); return Created($"/v1/providers/{providerId}/versions", result); }
    [HttpPost("{providerId:guid}/versions/{versionId:guid}/preview"), Authorize(Roles = "provider,admin")]
    public async Task<IActionResult> Preview(Guid providerId, Guid versionId, PreviewRequest request, CancellationToken ct) => Ok(await service.Preview(providerId, versionId, request, ct));
    [HttpPost("{providerId:guid}/versions/{versionId:guid}/publish"), Authorize(Roles = "provider,admin")]
    public async Task<IActionResult> Publish(Guid providerId, Guid versionId, CancellationToken ct)
    { await service.Publish(providerId, versionId, ct); return NoContent(); }
    [HttpPatch("{providerId:guid}/enabled"), Authorize(Roles = "provider,admin")]
    public async Task<IActionResult> SetEnabled(Guid providerId, EnabledRequest request, CancellationToken ct)
    { await service.SetEnabled(providerId, request.Enabled, ct); return NoContent(); }
}
