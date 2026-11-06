using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NotificationHub.Api.Services;

namespace NotificationHub.Api.Controllers;

[ApiController, Route("health"), AllowAnonymous]
public sealed class HealthController(IHealthService service) : ControllerBase
{
    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "alive" });
    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken ct) => await service.Ready(ct) ? Ok(new { status = "ready" }) : StatusCode(503);
}
