using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.IdentityModel.Tokens;

namespace DocVault.Api.Auth;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddDocVaultAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var authority = configuration["Keycloak:Authority"]
            ?? throw new InvalidOperationException("Keycloak:Authority must be configured.");
        var audience = configuration["Keycloak:Audience"]
            ?? throw new InvalidOperationException("Keycloak:Audience must be configured.");

        var requireHttpsMetadata = configuration.GetValue<bool?>("Keycloak:RequireHttpsMetadata")
            ?? !environment.IsDevelopment();

        // Fail fast, at startup, rather than silently trusting an unauthenticated
        // metadata endpoint in production. Loopback is exempt because that is the
        // local lab and traffic never leaves the machine.
        if (!requireHttpsMetadata && !IsLoopback(authority))
        {
            throw new InvalidOperationException(
                $"Keycloak:RequireHttpsMetadata is false but the authority '{authority}' is not loopback. " +
                "Fetching signing keys over plain HTTP from a remote host allows an attacker to substitute " +
                "their own keys and mint valid tokens. Use HTTPS.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // The Authority is the realm's issuer URL. JwtBearer appends
                // /.well-known/openid-configuration to discover the signing keys, and
                // refreshes them automatically — which is what makes Keycloak key
                // rotation a non-event for this API.
                options.Authority = authority;
                options.Audience = audience;

                // Plain HTTP is only tolerable against the local Docker container:
                // refusing to fetch metadata over HTTP prevents a trivial
                // man-in-the-middle on the signing keys.
                //
                // Explicit configuration rather than `!environment.IsDevelopment()`,
                // because the environment name is easy to lose (running `dotnet run
                // --no-launch-profile` silently makes it Production) and the failure is
                // an opaque 500 on every request. The guard below makes the unsafe
                // combination impossible to deploy by accident.
                options.RequireHttpsMetadata = requireHttpsMetadata;

                // CRITICAL. .NET's legacy inbound claim mapping rewrites JWT claim names
                // into long WS-Federation URIs: `sub` becomes `nameidentifier`, and
                // `realm_access` / `resource_access` become awkward to find. Turning it off
                // keeps the claims exactly as Keycloak issued them, which is what
                // KeycloakClaimsTransformation and TenantClaimExtensions expect.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,

                    // Requires the audience mapper configured in 20-realm/mappers.tf. If that
                    // mapper is missing, every request 401s with "audience is invalid".
                    ValidateAudience = true,
                    ValidAudience = audience,

                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    // Default is a very generous 5 minutes, which meaningfully extends the
                    // life of a stolen token. 30s is enough for realistic clock drift.
                    ClockSkew = TimeSpan.FromSeconds(30),

                    // Reject anything not signed with an asymmetric algorithm. This is the
                    // defence against `alg: none` and HMAC-confusion attacks, where an
                    // attacker signs a token using the public key as an HMAC secret.
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256],

                    NameClaimType = "preferred_username",
                    RoleClaimType = ClaimTypes.Role,
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        // Token validation failures are otherwise silent from the client's
                        // point of view. Logging the reason turns "mysterious 401" into a
                        // two-minute fix; see docs/12-troubleshooting.md.
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("DocVault.Auth");
                        logger.LogWarning(context.Exception, "JWT validation failed.");
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddSingleton<IClaimsTransformation, KeycloakClaimsTransformation>();
        services.AddSingleton<IAuthorizationHandler, StepUpAcrHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, StepUpAuthorizationResultHandler>();

        return services;
    }

    public static IServiceCollection AddDocVaultAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            // Role names come from resource_access["docvault-api"].roles.
            .AddPolicy(Policies.ReadDocuments, p => p.RequireRole("doc.reader", "doc.editor", "doc.admin"))
            .AddPolicy(Policies.WriteDocuments, p => p.RequireRole("doc.editor", "doc.admin"))
            .AddPolicy(Policies.AdminDocuments, p => p.RequireRole("doc.admin"))

            // Role AND a fresh MFA assertion. Both must hold.
            .AddPolicy(Policies.Classified, p =>
            {
                p.RequireRole("doc.admin");
                p.AddRequirements(new StepUpAcrRequirement("silver"));
            })

            // A realm role rather than a client role: platform operators are not
            // specific to this API.
            .AddPolicy(Policies.PlatformAdmin, p => p.RequireRole("platform-admin"))

            // Scope-gated rather than role-gated. Scopes describe what the CLIENT was
            // authorised to ask for; roles describe what the USER may do. The analytics
            // endpoint cares about both, but the scope is what consent governs.
            .AddPolicy(Policies.Analytics, p => p.RequireAssertion(ctx =>
                ctx.User.FindFirst("scope")?.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("analytics:read") == true));

        return services;
    }

    /// <summary>
    /// True for an authority that never leaves this machine (or the compose network),
    /// where plain-HTTP metadata carries no real interception risk.
    /// </summary>
    private static bool IsLoopback(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || string.Equals(uri.Host, "keycloak", StringComparison.OrdinalIgnoreCase));
}

public static class Policies
{
    public const string ReadDocuments = "documents:read";
    public const string WriteDocuments = "documents:write";
    public const string AdminDocuments = "documents:admin";
    public const string Classified = "documents:classified";
    public const string PlatformAdmin = "platform:admin";
    public const string Analytics = "analytics:read";
}
