using System.Text.Json;
using Crypton.Core.Fiat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Crypton.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
[AllowAnonymous]
[DisableRateLimiting]
public sealed class WebhooksController(FiatService fiat, ILogger<WebhooksController> logger) : ControllerBase
{
    /// <summary>Paystack event webhook. Configure this URL in the Paystack dashboard.</summary>
    [HttpPost("paystack")]
    [RequestSizeLimit(1_000_000)]
    public async Task<IActionResult> Paystack(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();

        if (!fiat.VerifyWebhookSignature(body, Request.Headers["x-paystack-signature"]))
        {
            logger.LogWarning("Rejected Paystack webhook with invalid signature from {Ip}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        using var doc = JsonDocument.Parse(body);
        await fiat.HandleWebhookAsync(doc.RootElement, ct);
        return Ok();
    }
}
