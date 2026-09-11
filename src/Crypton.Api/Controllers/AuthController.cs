using Crypton.Api.Auth;
using Crypton.Api.Contracts;
using Crypton.Api.Infrastructure;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Crypton.Api.Controllers;

[Route("api/auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(
    AuthService auth,
    TokenService tokens,
    UserManager<AppUser> users,
    AuditService audit,
    SessionValidator sessions,
    IOptions<JwtOptions> jwt) : ApiControllerBase
{
    public const string RefreshCookieName = "crypton_rt";
    public const string CsrfHeaderName = "X-Crypton-Client";

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        await auth.RegisterAsync(request.Email, request.Password, request.FirstName, request.LastName, HttpContext, ct);
        return Accepted(new { message = "If this email can be used, we've sent a confirmation link." });
    }

    [HttpPost("resend-confirmation")]
    public async Task<IActionResult> ResendConfirmation(EmailRequest request, CancellationToken ct)
    {
        await auth.ResendConfirmationAsync(request.Email, ct);
        return Accepted(new { message = "If the account exists and isn't confirmed, a new link is on its way." });
    }

    [HttpPost("confirm-email")]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest request, CancellationToken ct)
    {
        await auth.ConfirmEmailAsync(request.UserId, request.Token, ct);
        return Ok(new { confirmed = true });
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request.Email, request.Password, HttpContext, ct);
        return await ToResponseAsync(result);
    }

    [HttpPost("login/2fa")]
    public async Task<ActionResult<AuthResponse>> LoginTwoFactor(TwoFactorLoginRequest request, CancellationToken ct)
    {
        var result = await auth.CompleteTwoFactorAsync(request.ChallengeToken, request.Code, request.RecoveryCode, HttpContext, ct);
        return await ToResponseAsync(result);
    }

    [HttpPost("refresh")]
    [DisableRateLimiting]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken ct)
    {
        RequireCsrfHeader();
        if (!Request.Cookies.TryGetValue(RefreshCookieName, out var raw) || string.IsNullOrEmpty(raw))
        {
            throw new AppException(ErrorCodes.InvalidToken, "Your session has ended. Please sign in again.", 401);
        }

        var result = await tokens.RefreshAsync(raw, HttpContext.ClientIp(), HttpContext.UserAgent(), ct);
        switch (result.Outcome)
        {
            case RefreshOutcome.Success:
                SetRefreshCookie(result.Tokens!);
                return Ok(new AuthResponse(false, null, result.Tokens!.AccessToken, result.Tokens.AccessTokenExpiresAt, await ToUserDtoAsync(result.User!)));
            case RefreshOutcome.RaceRetry:
                throw new AppException("refresh_in_progress", "A refresh is already in progress. Retry shortly.", 409);
            case RefreshOutcome.ReuseDetected:
                ClearRefreshCookie();
                await audit.RecordNowAsync(AuditActions.TokenReuseDetected, null, HttpContext.Audit(), ct: ct);
                throw new AppException(ErrorCodes.InvalidToken, "Your session has ended. Please sign in again.", 401);
            default:
                ClearRefreshCookie();
                throw new AppException(ErrorCodes.InvalidToken, "Your session has ended. Please sign in again.", 401);
        }
    }

    [HttpPost("logout")]
    [DisableRateLimiting]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        RequireCsrfHeader();
        if (Request.Cookies.TryGetValue(RefreshCookieName, out var raw) && !string.IsNullOrEmpty(raw) &&
            await tokens.FindFamilyAsync(raw, ct) is { } familyId)
        {
            await tokens.RevokeFamilyAsync(familyId, "logout", ct);
            sessions.Invalidate(familyId);
        }

        ClearRefreshCookie();
        return NoContent();
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(EmailRequest request, CancellationToken ct)
    {
        await auth.ForgotPasswordAsync(request.Email, ct);
        return Accepted(new { message = "If an account exists for that email, a reset link is on its way." });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        await auth.ResetPasswordAsync(request.UserId, request.Token, request.NewPassword, HttpContext, ct);
        ClearRefreshCookie();
        return Ok(new { reset = true });
    }

    private async Task<ActionResult<AuthResponse>> ToResponseAsync(LoginResult result)
    {
        if (result.RequiresTwoFactor)
        {
            return Ok(new AuthResponse(true, result.ChallengeToken, null, null, null));
        }

        SetRefreshCookie(result.Tokens!);
        return Ok(new AuthResponse(false, null, result.Tokens!.AccessToken, result.Tokens.AccessTokenExpiresAt, await ToUserDtoAsync(result.User!)));
    }

    private async Task<UserDto> ToUserDtoAsync(AppUser user)
    {
        var fresh = await users.FindByIdAsync(user.Id.ToString()) ?? user;
        return AccountController.ToDto(fresh, await users.GetRolesAsync(fresh));
    }

    private void RequireCsrfHeader()
    {
        // Custom header forces a CORS preflight for cross-site requests; combined with SameSite cookies this blocks CSRF.
        if (!Request.Headers.ContainsKey(CsrfHeaderName))
        {
            throw new AppException("missing_client_header", $"The {CsrfHeaderName} header is required.", 400);
        }
    }

    private void SetRefreshCookie(IssuedTokens issued) =>
        Response.Cookies.Append(RefreshCookieName, issued.RefreshToken, CookieOptions(issued.RefreshTokenExpiresAt));

    private void ClearRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookieName, CookieOptions(null));

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = jwt.Value.RefreshCookieSecure,
        SameSite = string.Equals(jwt.Value.RefreshCookieSameSite, "None", StringComparison.OrdinalIgnoreCase) ? SameSiteMode.None : SameSiteMode.Strict,
        Path = "/api/auth",
        Expires = expires,
        IsEssential = true,
    };
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string Sensitive = "sensitive";
}
