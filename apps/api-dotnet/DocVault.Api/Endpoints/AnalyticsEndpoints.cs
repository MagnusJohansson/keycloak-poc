// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using DocVault.Api.Auth;
using DocVault.Api.Domain;

namespace DocVault.Api.Endpoints;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/analytics").WithTags("Analytics");

        // Use-case 5: consent and data minimisation.
        //
        // This endpoint returns counts, never rows. Combined with the pairwise `sub`
        // mapper on the analytics client, the caller cannot re-identify a user or
        // correlate them against the main application's records — even though it is a
        // perfectly ordinary OIDC client holding a perfectly ordinary token.
        group.MapGet("/usage", (ClaimsPrincipal user, IDocumentStore store) =>
        {
            var tenant = user.GetTenantContext();
            var documents = store.ListForTenant(tenant.Tenant, includeClassified: true);

            return Results.Ok(new
            {
                tenant = tenant.Tenant,
                totalDocuments = documents.Count,
                classifiedDocuments = documents.Count(d => d.IsClassified),
                distinctOwners = documents.Select(d => d.OwnerUsername).Distinct().Count(),

                // Shown only to make the privacy property observable in the demo: this is
                // the client-specific pseudonym, not the user's real subject id.
                pairwiseSubject = user.FindFirst("sub")?.Value,
            });
        })
        .RequireAuthorization(Policies.Analytics)
        .WithSummary("Aggregate usage counts. Requires the consented analytics:read scope.");

        // Required by the pairwise subject mapper: the OIDC spec has the provider fetch
        // this document to confirm which redirect URIs share a sector, and therefore
        // share a pseudonym.
        group.MapGet("/sector-identifier", (IConfiguration config) =>
            Results.Json(new[] { config["Keycloak:AnalyticsRedirectUri"] ?? "http://localhost:5001/analytics/callback" }))
            .AllowAnonymous()
            .WithSummary("OIDC sector identifier document for pairwise subject identifiers.");

        return app;
    }
}
