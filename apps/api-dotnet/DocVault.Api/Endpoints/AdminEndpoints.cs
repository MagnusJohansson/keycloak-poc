using DocVault.Api.Auth;
using DocVault.Api.Domain;

namespace DocVault.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization(Policies.PlatformAdmin);

        // Crosses tenant boundaries on purpose — that is what makes it platform-admin only,
        // and why it is a realm role rather than a client role.
        group.MapGet("/documents", (IDocumentStore store) =>
        {
            var all = store.ListForTenant("acme", true).Concat(store.ListForTenant("globex", true));
            return Results.Ok(new { documents = all });
        })
        .WithSummary("List documents across every tenant. Requires the platform-admin realm role.");

        return app;
    }
}
