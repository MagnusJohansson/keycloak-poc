// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using DocVault.Api.Auth;
using DocVault.Api.Domain;
using DocVault.Api.Sharing;
using Microsoft.Extensions.Options;

namespace DocVault.Api.Endpoints;

/// <summary>
/// A document's owner shares it with a person or a department of the same tenant (UC1:
/// "a document's owner can share it, regardless of role"). OpenFGA records the share;
/// Keycloak's Admin API says who the person is and whether they are in the tenant.
/// </summary>
public static class ShareEndpoints
{
    private const int MaxNameLength = 255;

    public static IEndpointRouteBuilder MapShareEndpoints(this IEndpointRouteBuilder app)
    {
        // Any document role: sharing is the owner's right, not a role's.
        var group = app.MapGroup("/documents/{id:guid}/shares")
            .WithTags("Sharing")
            .RequireAuthorization(Policies.ReadDocuments);

        group.MapGet("/", async (Guid id, ClaimsPrincipal user, IDocumentStore store, IDocumentAuthorizer authorizer,
            IPeopleDirectory directory, CancellationToken ct) =>
        {
            var (document, refusal) = await OwnedAsync(id, user, store, authorizer, ct);
            if (document is null)
            {
                return refusal!;
            }

            var shares = new List<object>();
            foreach (var share in await authorizer.SharesAsync(document, ct))
            {
                if (share is ShareTarget.Person p)
                {
                    // The person as they are now: the share is recorded by id, so a rename
                    // shows here. Someone who has left the tenant is still listed, so that
                    // the owner can remove the share, and marked: it grants them nothing
                    // now, and would again if they came back. A username of null is
                    // someone who no longer exists.
                    var person = await directory.SharedPersonAsync(p.UserId, document.Tenant, ct);
                    shares.Add(new { type = "user", id = p.UserId.ToString("D"), username = person?.Username, inTenant = person?.InTenant == true });
                }
                else if (share is ShareTarget.Department d)
                {
                    shares.Add(new { type = "department", name = d.Name });
                }
            }

            return Results.Ok(new { documentId = document.Id, visibility = document.Visibility, shares });
        })
        .WithSummary("Who a document is shared with. Owner only.");

        group.MapPost("/", async (Guid id, ShareRequest request, ClaimsPrincipal user, IDocumentStore store,
            IDocumentAuthorizer authorizer, IPeopleDirectory directory, IOptions<SharingSettings> settings,
            TimeProvider time, ILoggerFactory loggers, CancellationToken ct) =>
        {
            // One of the two, and a name that cannot be anything but a name.
            var username = request.Username?.Trim().ToLowerInvariant();
            var department = request.Department?.Trim();
            if ((username is null) == (department is null)
                || (username is not null && (username.Length is 0 or > MaxNameLength || username.Any(char.IsControl)))
                || (department is not null && !FgaNames.IsSafeName(department)))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid share",
                    detail: "Give either a username or a department name.");
            }

            var (document, refusal) = await OwnedAsync(id, user, store, authorizer, ct);
            if (document is null)
            {
                return refusal!;
            }

            // Only someone in the document's tenant. OpenFGA's model would make a share
            // to globex grant nothing anyway, but it would switch on the day that person
            // joined acme. "Not in your tenant" and "no such person" get the same answer,
            // so this cannot be used to find out who works elsewhere.
            ShareTarget target;
            if (username is not null)
            {
                // Every outcome takes at least the same time: see PersonLookupFloor.
                var started = time.GetTimestamp();
                var userId = await directory.FindInTenantAsync(username, document.Tenant, ct);
                var took = time.GetElapsedTime(started);
                var floor = settings.Value.PersonLookupFloor;
                if (took < floor)
                {
                    await Task.Delay(floor - took, time, ct);
                }
                else if (floor > TimeSpan.Zero)
                {
                    // Slower than the floor, so the time is the lookup's again. Said here so
                    // that a floor too low for this directory gets noticed.
                    loggers.CreateLogger("DocVault.Api.Sharing").LogWarning(
                        "A person lookup took {Took} ms, more than Sharing:PersonLookupFloor.", (int)took.TotalMilliseconds);
                }

                if (userId is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such person",
                        detail: "There is nobody with that username in your organisation.");
                }

                target = new ShareTarget.Person(userId.Value);
            }
            else
            {
                if (!await directory.DepartmentExistsAsync(document.Tenant, department!, ct))
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such department",
                        detail: "Your organisation has no department of that name.");
                }

                target = new ShareTarget.Department(department!);
            }

            await authorizer.ShareAsync(document, target, ct);
            return Results.Created($"/documents/{document.Id}/shares", target switch
            {
                ShareTarget.Person p => new { type = "user", id = p.UserId.ToString("D"), username, inTenant = true } as object,
                _ => new { type = "department", name = department },
            });
        })
        .WithSummary("Share a document with a person or a department of your tenant. Owner only.");

        // By id, not username: the id is what was recorded, and it still names someone
        // who has since been renamed, left the tenant, or been deleted.
        group.MapDelete("/users/{userId:guid}", async (Guid id, Guid userId, ClaimsPrincipal user, IDocumentStore store,
            IDocumentAuthorizer authorizer, CancellationToken ct) =>
        {
            var (document, refusal) = await OwnedAsync(id, user, store, authorizer, ct);
            if (document is null)
            {
                return refusal!;
            }

            // 204 whether or not there was such a share: removing it is idempotent.
            await authorizer.UnshareAsync(document, new ShareTarget.Person(userId), ct);
            return Results.NoContent();
        })
        .WithSummary("Stop sharing a document with a person. Owner only.");

        group.MapDelete("/departments/{name}", async (Guid id, string name, ClaimsPrincipal user, IDocumentStore store,
            IDocumentAuthorizer authorizer, CancellationToken ct) =>
        {
            if (!FgaNames.IsSafeName(name))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid department");
            }

            var (document, refusal) = await OwnedAsync(id, user, store, authorizer, ct);
            if (document is null)
            {
                return refusal!;
            }

            await authorizer.UnshareAsync(document, new ShareTarget.Department(name), ct);
            return Results.NoContent();
        })
        .WithSummary("Stop sharing a document with a department. Owner only.");

        return app;
    }

    /// <summary>
    /// The document if the caller may share it. Otherwise the answer to give: 404 when
    /// sharing is off or the caller cannot see the document (it might as well not exist),
    /// 403 when they can see it and are not its owner.
    /// </summary>
    private static async Task<(Document? Document, IResult? Refusal)> OwnedAsync(
        Guid id, ClaimsPrincipal user, IDocumentStore store, IDocumentAuthorizer authorizer, CancellationToken ct)
    {
        if (!authorizer.SupportsSharing)
        {
            return (null, Results.NotFound());
        }

        var (document, access) = await DocumentEndpoints.FindAsync(id, user, store, authorizer, ct);
        if (document is null)
        {
            return (null, Results.NotFound());
        }

        return access.CanShare
            ? (document, null)
            : (null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed",
                detail: "Only a document's owner can share it."));
    }
}

public sealed record ShareRequest(string? Username = null, string? Department = null);
