using System.Security.Claims;
using DocVault.Api.Auth;

namespace DocVault.Api.Endpoints;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        // Powers the React TokenInspector. Returning the server's view of the caller —
        // rather than letting the SPA decode the token itself — makes a whole class of
        // confusion visible: if the browser thinks you are a doc.editor but the API does
        // not, this endpoint shows you exactly where the two disagree.
        app.MapGet("/me", (ClaimsPrincipal user) =>
        {
            TenantContext tenant;
            string? tenantError = null;
            try
            {
                tenant = user.GetTenantContext();
            }
            catch (MultipleTenantsException ex)
            {
                tenant = TenantContext.None;
                tenantError = ex.Message;
            }

            return Results.Ok(new
            {
                subject = user.FindFirst("sub")?.Value,
                username = user.Identity?.Name,
                email = user.FindFirst("email")?.Value,
                tenant = tenant.Tenant,
                department = tenant.Department,
                tenantError,

                // The flattened roles, i.e. the output of KeycloakClaimsTransformation.
                roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Order().ToArray(),

                groups = user.FindAll("groups").Select(c => c.Value).ToArray(),
                acr = user.FindFirst("acr")?.Value,
                scopes = user.FindFirst("scope")?.Value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [],
                issuer = user.FindFirst("iss")?.Value,
                audience = user.FindAll("aud").Select(c => c.Value).ToArray(),
                expiresAt = user.FindFirst("exp")?.Value,
            });
        })
        .RequireAuthorization()
        .WithTags("Identity")
        .WithSummary("The API's view of the caller. The teaching endpoint.");

        // Deliberately anonymous, so the SPA can prove it can reach the API before
        // authentication is in the picture. Rules out "is it CORS or is it auth?".
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .AllowAnonymous()
            .WithTags("Diagnostics");

        return app;
    }
}
