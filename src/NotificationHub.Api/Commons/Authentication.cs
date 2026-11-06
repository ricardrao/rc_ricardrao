using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Commons;

public sealed class ApiKeyAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IConfiguration config)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var input = Request.Headers["X-Api-Key"].ToString();
        if (input.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        foreach (var client in config.GetSection("Clients").Get<ApiClient[]>() ?? [])
        {
            if (client.Key.Length < 16 || !CryptographicOperations.FixedTimeEquals(hash,
                    SHA256.HashData(Encoding.UTF8.GetBytes(client.Key)))) continue;
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, client.Id), new Claim(ClaimTypes.Role, client.Role) };
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => WriteFailure(401, "unauthorized");
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => WriteFailure(403, "forbidden");
    private Task WriteFailure(int status, string code)
    {
        Response.StatusCode = status;
        return Response.WriteAsJsonAsync(new { code, message = Context.RequestServices.GetRequiredService<MessageCatalog>().Get(code), traceId = Context.TraceIdentifier });
    }
}
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, MessageCatalog messages)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (context.Response.HasStarted) throw;
            var failure = ex as HubException;
            if (failure is null) logger.LogError(ex, "Request failed. Trace: {TraceId}", context.TraceIdentifier);
            context.Response.StatusCode = failure?.Status ?? 500;
            if (failure?.RetryAfter is int seconds) context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var code = failure?.Code ?? "internal_error";
            await context.Response.WriteAsJsonAsync(new { code, message = messages.Get(code), traceId = context.TraceIdentifier });
        }
    }
}
