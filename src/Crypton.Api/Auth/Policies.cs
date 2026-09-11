using Crypton.Core.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Crypton.Api.Auth;

public static class Policies
{
    public const string Staff = "staff";
    public const string Compliance = "compliance";
    public const string Admin = "admin";

    public static void Configure(AuthorizationOptions options, bool requireTwoFactor)
    {
        void Add(string name, params string[] roles) => options.AddPolicy(name, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole(roles)
            .RequireAssertion(ctx => !requireTwoFactor || ctx.User.HasClaim("mfa", "true")));

        Add(Staff, Roles.Admin, Roles.Compliance, Roles.Support);
        Add(Compliance, Roles.Admin, Roles.Compliance);
        Add(Admin, Roles.Admin);
    }
}
