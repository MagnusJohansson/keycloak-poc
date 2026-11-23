// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using DocVault.Api.Domain;

namespace DocVault.Api.Sharing;

/// <summary>What the caller may do to one document, as far as relationships go.</summary>
/// <remarks>
/// Roles are not in here. Whether someone may delete is "doc.admin, or the owner", and
/// the role half of that is read from the token by the endpoint, as every other role is.
/// </remarks>
public sealed record DocumentAccess(bool CanView, bool CanShare, bool CanDelete)
{
    public static readonly DocumentAccess None = new(false, false, false);
}

/// <summary>Someone a document is shared with.</summary>
public abstract record ShareTarget
{
    private ShareTarget()
    {
    }

    /// <summary>One person, by Keycloak user id: a username can be renamed, or reused.</summary>
    public sealed record Person(Guid UserId) : ShareTarget;

    /// <summary>Everyone in a department of the document's tenant, e.g. <c>legal</c>.</summary>
    public sealed record Department(string Name) : ShareTarget;
}

/// <summary>
/// Answers "may this caller see THIS document?". Every caller of these methods has
/// already checked that the document is in the caller's tenant: the API's own check,
/// kept so that tenant isolation does not rest on OpenFGA alone.
/// </summary>
public interface IDocumentAuthorizer
{
    /// <summary>False in <see cref="SharingMode.TenantOnly"/>: no private documents, no shares.</summary>
    bool SupportsSharing { get; }

    /// <summary>Which of these documents, all in the caller's tenant, the caller may see.</summary>
    Task<IReadOnlyList<Document>> VisibleAsync(ClaimsPrincipal user, IReadOnlyList<Document> candidates, CancellationToken cancellationToken);

    Task<DocumentAccess> AccessAsync(ClaimsPrincipal user, Document document, CancellationToken cancellationToken);

    /// <summary>Records a new document's tenant, owner and visibility.</summary>
    Task CreatedAsync(Document document, CancellationToken cancellationToken);

    /// <summary>Removes every relationship of a document. Called BEFORE it leaves the store.</summary>
    Task DeletingAsync(Document document, CancellationToken cancellationToken);

    Task ShareAsync(Document document, ShareTarget target, CancellationToken cancellationToken);

    Task UnshareAsync(Document document, ShareTarget target, CancellationToken cancellationToken);

    Task<IReadOnlyList<ShareTarget>> SharesAsync(Document document, CancellationToken cancellationToken);
}

/// <summary>OpenFGA decides (infra/openfga/docvault.fga).</summary>
public sealed class OpenFgaDocumentAuthorizer(OpenFgaClient fga) : IDocumentAuthorizer
{
    public bool SupportsSharing => true;

    public async Task<IReadOnlyList<Document>> VisibleAsync(ClaimsPrincipal user, IReadOnlyList<Document> candidates, CancellationToken cancellationToken)
    {
        var caller = Caller.From(user);
        if (caller is null || candidates.Count == 0)
        {
            return [];
        }

        var allowed = await fga.BatchCheckAsync(
            candidates.Select(d => new TupleKey(caller.User, "can_view", FgaNames.Document(d.Id))).ToList(),
            caller.Memberships, cancellationToken);

        return candidates.Where((_, i) => allowed[i]).ToList();
    }

    public async Task<DocumentAccess> AccessAsync(ClaimsPrincipal user, Document document, CancellationToken cancellationToken)
    {
        var caller = Caller.From(user);
        if (caller is null)
        {
            return DocumentAccess.None;
        }

        var @object = FgaNames.Document(document.Id);
        var allowed = await fga.BatchCheckAsync(
            [
                new(caller.User, "can_view", @object),
                new(caller.User, "can_share", @object),
                new(caller.User, "can_delete", @object),
            ],
            caller.Memberships, cancellationToken);

        return new DocumentAccess(allowed[0], allowed[1], allowed[2]);
    }

    public Task CreatedAsync(Document document, CancellationToken cancellationToken)
    {
        var @object = FgaNames.Document(document.Id);
        var tuples = new List<TupleKey> { new(FgaNames.Tenant(document.Tenant), "tenant", @object) };

        if (document.OwnerId is { } ownerId && Guid.TryParse(ownerId, out var owner))
        {
            tuples.Add(new(FgaNames.User(owner), "owner", @object));
        }

        if (document.Visibility == Visibility.Tenant)
        {
            tuples.Add(new(FgaNames.TenantMembers(document.Tenant), "viewer", @object));
        }

        return fga.WriteAsync(tuples, [], cancellationToken);
    }

    public async Task DeletingAsync(Document document, CancellationToken cancellationToken)
    {
        // Read, then delete what was read. A share written in between survives, on a
        // document about to disappear from the store: it grants nothing that exists.
        var tuples = await fga.ReadAsync(FgaNames.Document(document.Id), cancellationToken);
        await fga.WriteAsync([], tuples, cancellationToken);
    }

    public Task ShareAsync(Document document, ShareTarget target, CancellationToken cancellationToken) =>
        fga.WriteAsync([Viewer(document, target)], [], cancellationToken);

    public Task UnshareAsync(Document document, ShareTarget target, CancellationToken cancellationToken) =>
        fga.WriteAsync([], [Viewer(document, target)], cancellationToken);

    public async Task<IReadOnlyList<ShareTarget>> SharesAsync(Document document, CancellationToken cancellationToken)
    {
        var tuples = await fga.ReadAsync(FgaNames.Document(document.Id), cancellationToken);
        var userPrefix = "user:";
        var departmentPrefix = $"department:{document.Tenant}/";

        var shares = new List<ShareTarget>();
        foreach (var tuple in tuples.Where(t => t.Relation == "viewer"))
        {
            if (tuple.User.StartsWith(userPrefix, StringComparison.Ordinal)
                && Guid.TryParse(tuple.User[userPrefix.Length..], out var userId))
            {
                shares.Add(new ShareTarget.Person(userId));
            }
            else if (tuple.User.StartsWith(departmentPrefix, StringComparison.Ordinal)
                && tuple.User.EndsWith("#member", StringComparison.Ordinal))
            {
                shares.Add(new ShareTarget.Department(tuple.User[departmentPrefix.Length..^"#member".Length]));
            }

            // tenant:<t>#member is the document's visibility, not a share.
        }

        return shares;
    }

    private static TupleKey Viewer(Document document, ShareTarget target) => target switch
    {
        ShareTarget.Person person => new(FgaNames.User(person.UserId), "viewer", FgaNames.Document(document.Id)),
        ShareTarget.Department department => new(FgaNames.DepartmentMembers(document.Tenant, department.Name), "viewer", FgaNames.Document(document.Id)),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
}

/// <summary>
/// No OpenFGA: everyone in a tenant sees every document of it, and only the role decides
/// a delete. The lab as it was before sharing, and what Azure runs.
/// </summary>
public sealed class TenantOnlyAuthorizer : IDocumentAuthorizer
{
    public bool SupportsSharing => false;

    public Task<IReadOnlyList<Document>> VisibleAsync(ClaimsPrincipal user, IReadOnlyList<Document> candidates, CancellationToken cancellationToken) =>
        Task.FromResult(candidates);

    public Task<DocumentAccess> AccessAsync(ClaimsPrincipal user, Document document, CancellationToken cancellationToken) =>
        Task.FromResult(new DocumentAccess(CanView: true, CanShare: false, CanDelete: false));

    public Task CreatedAsync(Document document, CancellationToken cancellationToken) =>
        document.Visibility == Visibility.Tenant
            ? Task.CompletedTask
            // The endpoint refuses a private document first. If that check is ever lost,
            // this stops the document being created visible to the whole tenant.
            : throw new InvalidOperationException("A private document needs Sharing:Mode OpenFga.");

    public Task DeletingAsync(Document document, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShareAsync(Document document, ShareTarget target, CancellationToken cancellationToken) => throw NoSharing();

    public Task UnshareAsync(Document document, ShareTarget target, CancellationToken cancellationToken) => throw NoSharing();

    public Task<IReadOnlyList<ShareTarget>> SharesAsync(Document document, CancellationToken cancellationToken) => throw NoSharing();

    private static InvalidOperationException NoSharing() => new("Sharing needs Sharing:Mode OpenFga.");
}
