using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Crypton.Api.Contracts;
using Crypton.Core.Domain;
using Crypton.Core.Security;
using Crypton.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Tests.Integration;

[Collection(ApiCollection.Name)]
public class AuthTests(CryptonFactory factory)
{
    [Fact]
    public async Task Register_confirm_and_sign_in()
    {
        var client = factory.CreateApiClient();
        var email = $"new_{Guid.NewGuid():N}@test.crypton.local";
        using (var response = await client.PostAsync("/api/auth/register", JsonBody(new { email, password = "Strong-Password-1", firstName = "Ngozi", lastName = "Eze" })))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var early = await client.PostProblem("/api/auth/login", new { email, password = "Strong-Password-1" });
        Assert.Equal(HttpStatusCode.Forbidden, early.Status);
        Assert.Equal("email_not_confirmed", early.Code);

        var link = await LatestEmailLinkAsync(email, "confirm-email");
        var query = HttpUtility.ParseQueryString(new Uri(link).Query);
        await client.PostOk<object>("/api/auth/confirm-email", new { userId = query["userId"], token = query["token"] });

        var login = await client.PostOk<AuthResponse>("/api/auth/login", new { email, password = "Strong-Password-1" });
        Assert.False(login.RequiresTwoFactor);
        Assert.NotNull(login.AccessToken);
        Assert.Equal("Ngozi", login.User!.FirstName);

        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        var me = await client.GetOk<MeResponse>("/api/me");
        Assert.Equal(email, me.User.Email);
        Assert.Equal(0, me.User.KycTier);
    }

    [Fact]
    public async Task Duplicate_registration_does_not_reveal_the_account()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var client = factory.CreateApiClient();
        using var response = await client.PostAsync("/api/auth/register", JsonBody(new { email = user.Email, password = "Another-Password-1", firstName = "Else", lastName = "One" }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Weak_passwords_are_rejected()
    {
        var client = factory.CreateApiClient();
        var problem = await client.PostProblem("/api/auth/register", new { email = $"weak_{Guid.NewGuid():N}@test.crypton.local", password = "alllowercase", firstName = "Weak", lastName = "Pass" });
        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
    }

    [Fact]
    public async Task Lockout_after_repeated_failures()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var client = factory.CreateApiClient();
        for (var i = 0; i < 4; i++)
        {
            var failed = await client.PostProblem("/api/auth/login", new { email = user.Email, password = "Wrong-Password-1" });
            Assert.Equal("invalid_credentials", failed.Code);
        }

        var locked = await client.PostProblem("/api/auth/login", new { email = user.Email, password = "Wrong-Password-1" });
        Assert.Equal("locked_out", locked.Code);

        var evenCorrect = await client.PostProblem("/api/auth/login", new { email = user.Email, password = user.Password });
        Assert.Equal("locked_out", evenCorrect.Code);
    }

    [Fact]
    public async Task Refresh_rotates_and_detects_reuse()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Crypton-Client", "tests");

        using var login = await client.PostAsync("/api/auth/login", JsonBody(new { email = user.Email, password = user.Password }));
        login.EnsureSuccessStatusCode();
        var original = RefreshCookie(login);

        using var rotated = await RefreshWith(client, original);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var replacement = RefreshCookie(rotated);
        Assert.NotEqual(original, replacement);

        // Within a few seconds a second use is treated as a benign race (e.g. two tabs), not theft.
        using (var race = await RefreshWith(client, original))
        {
            Assert.Equal(HttpStatusCode.Conflict, race.StatusCode);
        }

        // Presenting the old token again later is treated as theft: the whole session is revoked.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var reuse = await RefreshWith(client, original))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        }

        using var legit = await RefreshWith(client, replacement);
        Assert.Equal(HttpStatusCode.Unauthorized, legit.StatusCode);
    }

    private static async Task<HttpResponseMessage> RefreshWith(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"crypton_rt={token}");
        return await client.SendAsync(request);
    }

    private static string RefreshCookie(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie").First(h => h.StartsWith("crypton_rt=", StringComparison.Ordinal));
        return header["crypton_rt=".Length..header.IndexOf(';')];
    }

    [Fact]
    public async Task Refresh_requires_client_header()
    {
        var user = await TestAccounts.CreateAsync(factory);
        user.Client.DefaultRequestHeaders.Remove("X-Crypton-Client");
        var problem = await user.Client.PostProblem("/api/auth/refresh");
        Assert.Equal("missing_client_header", problem.Code);
    }

    [Fact]
    public async Task Logout_revokes_the_session_immediately()
    {
        var user = await TestAccounts.CreateAsync(factory);
        await user.Client.GetOk<MeResponse>("/api/me");
        using (var logout = await user.Client.PostAsync("/api/auth/logout", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using var after = await user.Client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Two_factor_enrolment_login_and_replay_protection()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var setup = await user.Client.PostOk<TwoFactorSetupResponse>("/api/me/2fa/setup");
        var key = setup.SharedKey.Replace(" ", "").ToUpperInvariant();
        Assert.Contains("otpauth://totp/", setup.OtpAuthUri);

        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        var code = Totp.Compute(Base32.Decode(key), Totp.TimeStep(factory.Clock.GetUtcNow()));
        var codes = await user.Client.PostOk<RecoveryCodesResponse>("/api/me/2fa/enable", new { code });
        Assert.Equal(10, codes.RecoveryCodes.Count);

        var client = factory.CreateApiClient();
        var first = await client.PostOk<AuthResponse>("/api/auth/login", new { email = user.Email, password = user.Password });
        Assert.True(first.RequiresTwoFactor);
        Assert.Null(first.AccessToken);

        // The code used for enrolment cannot be replayed within the same time step.
        var replay = await client.PostProblem("/api/auth/login/2fa", new { challengeToken = first.ChallengeToken, code });
        Assert.Equal("invalid_two_factor_code", replay.Code);

        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        var fresh = Totp.Compute(Base32.Decode(key), Totp.TimeStep(factory.Clock.GetUtcNow()));
        var second = await client.PostOk<AuthResponse>("/api/auth/login/2fa", new { challengeToken = first.ChallengeToken, code = fresh });
        Assert.NotNull(second.AccessToken);

        var third = await client.PostOk<AuthResponse>("/api/auth/login", new { email = user.Email, password = user.Password });
        var viaRecovery = await client.PostOk<AuthResponse>("/api/auth/login/2fa", new { challengeToken = third.ChallengeToken, recoveryCode = codes.RecoveryCodes[0] });
        Assert.NotNull(viaRecovery.AccessToken);

        var fourth = await client.PostOk<AuthResponse>("/api/auth/login", new { email = user.Email, password = user.Password });
        var reusedRecovery = await client.PostProblem("/api/auth/login/2fa", new { challengeToken = fourth.ChallengeToken, recoveryCode = codes.RecoveryCodes[0] });
        Assert.Equal("invalid_two_factor_code", reusedRecovery.Code);
    }

    [Fact]
    public async Task Password_reset_signs_out_everywhere_and_pauses_withdrawals()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var anonymous = factory.CreateApiClient();
        using (var forgot = await anonymous.PostAsync("/api/auth/forgot-password", JsonBody(new { email = user.Email })))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        var link = await LatestEmailLinkAsync(user.Email, "reset-password");
        var query = HttpUtility.ParseQueryString(new Uri(link).Query);
        await anonymous.PostOk<object>("/api/auth/reset-password", new { userId = query["userId"], token = query["token"], newPassword = "Brand-New-Password-7" });

        using var me = await user.Client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        var locked = await factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.WithdrawalsLockedUntil).FirstAsync());
        Assert.NotNull(locked);
        Assert.True(locked > factory.Clock.GetUtcNow().AddHours(23));

        var login = await anonymous.PostOk<AuthResponse>("/api/auth/login", new { email = user.Email, password = "Brand-New-Password-7" });
        Assert.NotNull(login.AccessToken);

        var reused = await anonymous.PostProblem("/api/auth/reset-password", new { userId = query["userId"], token = query["token"], newPassword = "Another-New-Password-8" });
        Assert.Equal("invalid_token", reused.Code);
    }

    [Fact]
    public async Task Admin_endpoints_require_staff_role()
    {
        var user = await TestAccounts.CreateAsync(factory);
        using var response = await user.Client.GetAsync("/api/admin/dashboard");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var admin = await TestAccounts.AdminAsync(factory);
        var dashboard = await admin.Client.GetOk<AdminDashboardDto>("/api/admin/dashboard");
        Assert.Equal("Simulated", dashboard.BlockchainMode);
    }

    private async Task<string> LatestEmailLinkAsync(string email, string pathFragment)
    {
        var body = await factory.WithDbAsync(db => db.EmailOutbox
            .Where(m => m.ToEmail == email)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.TextBody)
            .FirstAsync());
        var match = Regex.Match(body, @"https?://\S*" + Regex.Escape(pathFragment) + @"\S*");
        Assert.True(match.Success, $"No {pathFragment} link in email: {body}");
        return match.Value;
    }

    private static StringContent JsonBody(object value) =>
        new(System.Text.Json.JsonSerializer.Serialize(value, Http.Json), System.Text.Encoding.UTF8, "application/json");
}
