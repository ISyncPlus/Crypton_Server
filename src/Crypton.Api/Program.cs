using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Crypton.Api.Auth;
using Crypton.Api.Controllers;
using Crypton.Api.Infrastructure;
using Crypton.Api.Jobs;
using Crypton.Api.Seeding;
using Crypton.Core;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Wallets;
using Crypton.Integrations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
var config = builder.Configuration;

// ----------------------------------------------------------------- core services
builder.Services.AddCryptonCore(config);
builder.Services.AddCryptonIntegrations(config);

builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
builder.Services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));
builder.Services.Configure<JobsOptions>(config.GetSection(JobsOptions.Section));
builder.Services.Configure<DevOptions>(config.GetSection(DevOptions.Section));

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SessionValidator>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DbSeeder>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());

builder.Services.AddDataProtection()
    .SetApplicationName("Crypton")
    .PersistKeysToFileSystem(new DirectoryInfo(config["DataProtection:KeysPath"] ?? "data/keys"));

// ----------------------------------------------------------------- identity & auth
builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<AppRole>()
    .AddEntityFrameworkStores<CryptonDbContext>()
    .AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(24));

var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwt.GetSigningKey(),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var userId) || !Guid.TryParse(principal.FindFirst("sid")?.Value, out var sessionId))
                {
                    context.Fail("Malformed token.");
                    return;
                }

                var validator = context.HttpContext.RequestServices.GetRequiredService<SessionValidator>();
                if (!await validator.IsValidAsync(userId, sessionId, context.HttpContext.RequestAborted))
                {
                    context.Fail("Session is no longer active.");
                }
            },
        };
    });

// Validate token lifetimes against the application clock (the same TimeProvider that issues them).
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TimeProvider>((options, clock) =>
    {
        options.TokenValidationParameters.LifetimeValidator = (notBefore, expires, _, parameters) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (notBefore is { } nbf && now + parameters.ClockSkew < nbf.ToUniversalTime())
            {
                return false;
            }

            return expires is not { } exp || now - parameters.ClockSkew < exp.ToUniversalTime();
        };
    });

builder.Services.AddAuthorization(options => Policies.Configure(options, config.GetValue("Security:RequireTwoFactorForStaff", true)));

// ----------------------------------------------------------------- web
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.Converters.Add(new DecimalStringConverter());
        options.JsonSerializerOptions.Converters.Add(new NullableDecimalStringConverter());
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var problem = new ValidationProblemDetails(context.ModelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Please check the highlighted fields.",
                Type = "https://docs.crypton.local/errors/validation_error",
            };
            problem.Extensions["code"] = "validation_error";
            return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
        };
    });

var rateLimitingEnabled = config.GetValue("RateLimiting:Enabled", true);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        var problem = ProblemExceptionHandler.Create(429, "rate_limited", "Too many requests. Please slow down and try again shortly.");
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(problem, ct);
    };

    string ClientKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    string UserKey(HttpContext http) => http.User.FindFirst("sub")?.Value ?? ClientKey(http);

    options.AddPolicy(RateLimitPolicies.Auth, http => rateLimitingEnabled
        ? RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimiting:AuthPerMinute", 20), Window = TimeSpan.FromMinutes(1) })
        : RateLimitPartition.GetNoLimiter("off"));
    options.AddPolicy(RateLimitPolicies.Sensitive, http => rateLimitingEnabled
        ? RateLimitPartition.GetSlidingWindowLimiter(UserKey(http), _ => new SlidingWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimiting:SensitivePerMinute", 90), Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6 })
        : RateLimitPartition.GetNoLimiter("off"));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => rateLimitingEnabled
        ? RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimiting:GlobalPerMinute", 900), Window = TimeSpan.FromMinutes(1) })
        : RateLimitPartition.GetNoLimiter("off"));
});

var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }
}));

builder.Services.AddOpenApi();

var app = builder.Build();

// ----------------------------------------------------------------- safety checks
var blockchain = config.GetSection(BlockchainOptions.Section).Get<BlockchainOptions>() ?? new BlockchainOptions();
var payments = config.GetSection(PaymentsOptions.Section).Get<PaymentsOptions>() ?? new PaymentsOptions();
if (app.Environment.IsProduction())
{
    var simulated = blockchain.IsSimulated || string.Equals(payments.Provider, "Simulated", StringComparison.OrdinalIgnoreCase);
    if (simulated && !config.GetValue("App:AllowSimulationInProduction", false))
    {
        throw new InvalidOperationException("Simulated blockchain or payments are configured in Production. Set Blockchain:Mode=Live and Payments:Provider=Paystack, or App:AllowSimulationInProduction=true for a public demo.");
    }

    if (config.GetValue("Dev:EnableEndpoints", false))
    {
        throw new InvalidOperationException("Dev:EnableEndpoints must be false in Production.");
    }
}

// ----------------------------------------------------------------- database
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CryptonDbContext>();
    if (config.GetValue("Database:MigrateOnStartup", true))
    {
        await db.Database.MigrateAsync();
    }

    await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync(CancellationToken.None);
}

// ----------------------------------------------------------------- pipeline
if (config.GetValue("App:TrustForwardedHeaders", false))
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    forwarded.KnownProxies.Clear();
#pragma warning disable CS0618, ASPDEPR005
    forwarded.KnownNetworks.Clear();
#pragma warning restore CS0618, ASPDEPR005
    app.UseForwardedHeaders(forwarded);
}

app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().DisableRateLimiting();
app.MapGet("/health/ready", async (CryptonDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503))
    .AllowAnonymous()
    .DisableRateLimiting();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.Run();

public partial class Program;
