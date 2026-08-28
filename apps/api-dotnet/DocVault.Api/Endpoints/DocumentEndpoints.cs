// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using DocVault.Api.Auth;
using DocVault.Api.Domain;

namespace DocVault.Api.Endpoints;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/documents").WithTags("Documents");

        // --- List -------------------------------------------------------------
        // Every caller hits the same code. What differs is the tenant derived from
        // their token, so tenant isolation is enforced in one place rather than
        // remembered at each call site.
        group.MapGet("/", (ClaimsPrincipal user, IDocumentStore store) =>
        {
            var tenant = user.GetTenantContext();
            if (!tenant.IsResolved)
            {
                // Authenticated, but belongs to no tenant. 403 — not 401: the caller
                // proved who they are, they simply may not do this.
                return Results.Problem(
                    title: "No tenant",
                    detail: "Your account is not a member of any tenant group.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var documents = store.ListForTenant(tenant.Tenant, includeClassified: false);
            return Results.Ok(new
            {
                tenant = tenant.Tenant,
                department = tenant.Department,
                documents,
            });
        })
        .RequireAuthorization(Policies.ReadDocuments)
        .WithSummary("List documents in the caller's tenant.");

        // --- Create -----------------------------------------------------------
        group.MapPost("/", (CreateDocumentRequest request, ClaimsPrincipal user, IDocumentStore store) =>
        {
            var tenant = user.GetTenantContext();
            if (!tenant.IsResolved)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "No tenant");
            }

            // The tenant comes from the TOKEN, never from the request body. Trusting a
            // client-supplied tenant id is how cross-tenant writes happen.
            var document = store.Add(
                tenant.Tenant,
                request.Title,
                user.Identity?.Name ?? "unknown",
                isClassified: false);

            return Results.Created($"/documents/{document.Id}", document);
        })
        .RequireAuthorization(Policies.WriteDocuments)
        .WithSummary("Create a document. Requires doc.editor or doc.admin.");

        // --- Delete -----------------------------------------------------------
        group.MapDelete("/{id:guid}", (Guid id, ClaimsPrincipal user, IDocumentStore store) =>
        {
            var tenant = user.GetTenantContext();
            var document = store.Get(id);

            // Return 404, not 403, for a document in another tenant. A 403 would confirm
            // the document exists, leaking information across the tenant boundary.
            if (document is null || document.Tenant != tenant.Tenant)
            {
                return Results.NotFound();
            }

            store.Delete(id);
            return Results.NoContent();
        })
        .RequireAuthorization(Policies.AdminDocuments)
        .WithSummary("Delete a document. Requires doc.admin.");

        // --- Classified -------------------------------------------------------
        // The step-up demo. doc.admin alone is not enough; the token must also carry
        // acr=silver, meaning the user completed OTP recently.
        group.MapGet("/classified", (ClaimsPrincipal user, IDocumentStore store) =>
        {
            var tenant = user.GetTenantContext();
            var documents = store.ListForTenant(tenant.Tenant, includeClassified: true)
                .Where(d => d.IsClassified);

            return Results.Ok(new { tenant = tenant.Tenant, acr = user.FindFirst("acr")?.Value, documents });
        })
        .RequireAuthorization(Policies.Classified)
        .WithSummary("List classified documents. Requires doc.admin AND acr=silver (MFA step-up).");

        return app;
    }
}

public sealed record CreateDocumentRequest(string Title);
