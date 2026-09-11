using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Crypton.Core.Audit;
using Crypton.Core.Wallets;

namespace Crypton.Api.Infrastructure;

public static class HttpContextExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : throw new UnauthorizedAccessException("No user id in token.");

    public static Guid? SessionId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sid"), out var id) ? id : null;

    public static string? ClientIp(this HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    public static string UserAgent(this HttpContext context)
    {
        var ua = context.Request.Headers.UserAgent.ToString();
        return ua.Length > 512 ? ua[..512] : ua;
    }

    public static string DeviceHash(this HttpContext context) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context.UserAgent())))[..32];

    public static AuditContext Audit(this HttpContext context) =>
        new(context.User.Identity?.IsAuthenticated == true ? context.User.UserId() : null, context.ClientIp());

    public static RequestContext RequestInfo(this HttpContext context) => new(context.ClientIp(), context.DeviceHash());
}
