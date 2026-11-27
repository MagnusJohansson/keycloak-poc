// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using DocVault.Api.Auth;
using DocVault.Api.Domain;
using DocVault.Api.Sharing;

namespace DocVault.Api.Endpoints;

/// <summary>
/// Documents. Four questions, each answered by the part that owns it:
/// <list type="bullet">
/// <item>which tenant you are in: the token's group path (<see cref="TenantClaimExtensions"/>);</item>
/// <item>what kind of thing you may do: the token's roles (<see cref="Policies"/>);</item>
/// <item>whether the sign-in was strong enough: the token's <c>acr</c> (classified only);</item>
/// <item>whether you may do it to THIS document: OpenFGA (<see cref="IDocumentAuthorizer"/>).</item>
/// </list>
/// </summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/documents").WithTags("Documents");

        // --- List -------------------------------------------------------------
        // Every caller hits the same code. What differs is the tenant derived from
        // their token, so tenant isolation is enforced in one place rather than
        // remembered at each call site.
        group.MapGet("/", async (ClaimsPrincipal user, IDocumentStore store, IDocumentAuthorizer authorizer, CancellationToken ct) =>
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

            // The store narrows to the tenant; OpenFGA decides each of those. Not
            // OpenFGA's list-objects, which drops results past its limit with a 200.
            var documents = await authorizer.VisibleAsync(
                user, store.ListForTenant(tenant.Tenant, includeClassified: false), ct);

            var sub = user.FindFirst("sub")?.Value;
            return Results.Ok(new
            {
                tenant = tenant.Tenant,
                department = tenant.Department,
                documents = documents.Select(d => DocumentView.Of(d, sub)),
            });
        })
        .RequireAuthorization(Policies.ReadDocuments)
        .WithSummary("List the documents in the caller's tenant that the caller may see.");

        // --- One document -----------------------------------------------------
        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IDocumentStore store, IDocumentAuthorizer authorizer, CancellationToken ct) =>
        {
            var (document, _) = await FindAsync(id, user, store, authorizer, ct);
            return document is null ? Results.NotFound() : Results.Ok(DocumentView.Of(document, user.FindFirst("sub")?.Value));
        })
        .RequireAuthorization(Policies.ReadDocuments)
        .WithSummary("One document. 404 for one in another tenant AND for one you may not see.");

        // --- Create -----------------------------------------------------------
        group.MapPost("/", async (CreateDocumentRequest request, ClaimsPrincipal user, IDocumentStore store, IDocumentAuthorizer authorizer, CancellationToken ct) =>
        {
            var tenant = user.GetTenantContext();
            if (!tenant.IsResolved)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "No tenant");
            }

            var visibility = request.Visibility ?? Visibility.Tenant;
            if (visibility == Visibility.Private && !authorizer.SupportsSharing)
            {
                // Refused, not created visible to the whole tenant.
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Private documents are not available",
                    detail: "This deployment has no per-document authorization (Sharing:Mode is TenantOnly).");
            }

            // The owner is recorded by Keycloak user id. A token without one could own a
            // document nobody, the owner included, could ever be shown.
            var caller = Caller.From(user);
            if (authorizer.SupportsSharing && caller is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "No subject",
                    detail: "Your token does not identify you as a Keycloak user.");
            }

            // The tenant comes from the TOKEN, never from the request body. Trusting a
            // client-supplied tenant id is how cross-tenant writes happen.
            var document = store.Add(
                tenant.Tenant,
                request.Title,
                user.Identity?.Name ?? "unknown",
                caller?.UserId.ToString("D"),
                visibility,
                isClassified: false);

            try
            {
                await authorizer.CreatedAsync(document, ct);
            }
            catch
            {
                // Without its tuples the document could be seen by nobody. Do not leave it
                // half-made: take it out again, and let the error answer (503).
                store.Delete(document.Id);
                throw;
            }

            return Results.Created($"/documents/{document.Id}", DocumentView.Of(document, user.FindFirst("sub")?.Value));
        })
        .RequireAuthorization(Policies.WriteDocuments)
        .WithSummary("Create a document, visible to the tenant (default) or private. Requires doc.editor or doc.admin.");

        // --- Delete -----------------------------------------------------------
        // An administrator, or the owner. Either way only a document you can see: an
        // administrator cannot delete a private document they cannot see, and gets 404,
        // as for one that does not exist.
        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IDocumentStore store, IDocumentAuthorizer authorizer, CancellationToken ct) =>
        {
            // Classified documents only for doc.admin, who could always delete them. For
            // anyone else a classified id is a 404, as on GET: a 403 would confirm that a
            // document behind the step-up policy exists (found by review).
            var (document, access) = await FindAsync(id, user, store, authorizer, ct, includeClassified: user.IsInRole("doc.admin"));
            if (document is null)
            {
                return Results.NotFound();
            }

            if (!user.IsInRole("doc.admin") && !access.CanDelete)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed",
                    detail: "Deleting a document needs the doc.admin role, or being its owner.");
            }

            // Relationships first. If that fails the document is still there, intact; the
            // other order could leave a document in the store that nobody may see.
            await authorizer.DeletingAsync(user, document, ct);
            store.Delete(id);
            return Results.NoContent();
        })
        .RequireAuthorization(Policies.ReadDocuments)
        .WithSummary("Delete a document. Requires doc.admin, or owning it.");

        // --- Classified -------------------------------------------------------
        // The step-up demo. doc.admin alone is not enough; the token must also carry
        // acr=silver, meaning the user completed OTP recently. Classified documents are
        // all seeded and visible to their tenant, so OpenFGA is not asked here.
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

    /// <summary>
    /// The document, if it is in the caller's tenant AND the caller may see it; otherwise
    /// null, for every reason alike. Callers answer null with 404, so that "exists but not
    /// yours" and "does not exist" cannot be told apart.
    /// </summary>
    /// <remarks>
    /// The tenant is compared here, before OpenFGA is asked, and OpenFGA's model requires
    /// the tenant too. Two checks on purpose: either alone holds the boundary.
    /// A classified document is only reachable through /classified, behind the step-up
    /// policy, unless <paramref name="includeClassified"/> (delete by a doc.admin, which
    /// has never asked for step-up).
    /// </remarks>
    internal static async Task<(Document? Document, DocumentAccess Access)> FindAsync(
        Guid id, ClaimsPrincipal user, IDocumentStore store, IDocumentAuthorizer authorizer,
        CancellationToken ct, bool includeClassified = false)
    {
        var tenant = user.GetTenantContext();
        var document = store.Get(id);
        if (document is null || !tenant.IsResolved || document.Tenant != tenant.Tenant
            || (document.IsClassified && !includeClassified))
        {
            return (null, DocumentAccess.None);
        }

        var access = await authorizer.AccessAsync(user, document, ct);
        return access.CanView ? (document, access) : (null, DocumentAccess.None);
    }
}

public sealed record CreateDocumentRequest(string Title, Visibility? Visibility = null);
