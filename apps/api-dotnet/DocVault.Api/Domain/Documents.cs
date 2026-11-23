// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace DocVault.Api.Domain;

/// <summary>Who a new document is visible to before anyone shares it.</summary>
public enum Visibility
{
    /// <summary>Everyone in the tenant: what every document was before sharing existed.</summary>
    Tenant,

    /// <summary>Its owner, and whoever the owner shares it with.</summary>
    Private,
}

public sealed record Document(
    Guid Id,
    string Tenant,
    string Title,
    string OwnerUsername,
    // The owner's Keycloak user id, which is what OpenFGA records. Not in responses:
    // the username is for people, the id is for decisions. Null for the seeded
    // documents, whose owners' ids Keycloak chooses when the realm is created.
    [property: JsonIgnore] string? OwnerId,
    Visibility Visibility,
    bool IsClassified,
    DateTimeOffset CreatedAt);

/// <summary>A document as one caller is shown it.</summary>
/// <param name="IsOwner">
/// Whether the caller is the owner the store recorded: for a client to decide whether to
/// offer "Share". A hint, not a decision: OpenFGA decides, and a seeded document, which
/// has no recorded owner, is nobody's.
/// </param>
public sealed record DocumentView(
    Guid Id, string Tenant, string Title, string OwnerUsername, Visibility Visibility,
    bool IsClassified, DateTimeOffset CreatedAt, bool IsOwner)
{
    public static DocumentView Of(Document d, string? callerSub) => new(
        d.Id, d.Tenant, d.Title, d.OwnerUsername, d.Visibility, d.IsClassified, d.CreatedAt,
        IsOwner: d.OwnerId is not null && Guid.TryParse(callerSub, out var sub) && d.OwnerId == sub.ToString("D"));
}

public interface IDocumentStore
{
    IReadOnlyList<Document> ListForTenant(string tenant, bool includeClassified);
    Document? Get(Guid id);
    Document Add(string tenant, string title, string ownerUsername, string? ownerId, Visibility visibility, bool isClassified);
    bool Delete(Guid id);
}

/// <summary>
/// Deliberately in-memory. This lab is about identity, not persistence — a database would
/// add setup cost without teaching anything about Keycloak.
/// </summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    /// <summary>
    /// Fixed ids for the seeded documents, so the tuples written for them at startup are the
    /// same on every start. Random ids would leave a new set behind in OpenFGA each time.
    /// </summary>
    public static class Seeded
    {
        public static readonly Guid AcmeRoadmap = new("d0c00000-0000-4000-8000-000000000001");
        public static readonly Guid AcmeHandbook = new("d0c00000-0000-4000-8000-000000000002");
        public static readonly Guid AcmeMemo = new("d0c00000-0000-4000-8000-000000000003");
        public static readonly Guid GlobexSuppliers = new("d0c00000-0000-4000-8000-000000000004");
    }

    private readonly ConcurrentDictionary<Guid, Document> _documents = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public InMemoryDocumentStore()
    {
        // Seeded so tenant isolation is visible immediately: alice (acme) and bob (globex)
        // see disjoint lists using the exact same endpoint and the exact same code path.
        // Visible to the whole tenant, with no owner OpenFGA knows of: nobody can share
        // them, and only an administrator can delete them.
        Seed(Seeded.AcmeRoadmap, "acme", "Acme Q3 Roadmap", "alice", isClassified: false);
        Seed(Seeded.AcmeHandbook, "acme", "Acme Engineering Handbook", "alice", isClassified: false);
        Seed(Seeded.AcmeMemo, "acme", "Acme Acquisition Memo", "carol", isClassified: true);
        Seed(Seeded.GlobexSuppliers, "globex", "Globex Supplier List", "bob", isClassified: false);
    }

    public IReadOnlyList<Document> ListForTenant(string tenant, bool includeClassified) =>
        _documents.Values
            .Where(d => d.Tenant == tenant && (includeClassified || !d.IsClassified))
            .OrderBy(d => d.CreatedAt)
            .ToList();

    public Document? Get(Guid id) => _documents.GetValueOrDefault(id);

    public Document Add(string tenant, string title, string ownerUsername, string? ownerId, Visibility visibility, bool isClassified)
    {
        var document = new Document(Guid.NewGuid(), tenant, title, ownerUsername, ownerId, visibility, isClassified, DateTimeOffset.UtcNow);
        _documents[document.Id] = document;
        return document;
    }

    public bool Delete(Guid id) => _documents.TryRemove(id, out _);

    private void Seed(Guid id, string tenant, string title, string ownerUsername, bool isClassified)
    {
        // Spaced a millisecond apart so the list order is the order written here.
        var createdAt = _startedAt.AddMilliseconds(_documents.Count);
        _documents[id] = new Document(id, tenant, title, ownerUsername, OwnerId: null, Visibility.Tenant, isClassified, createdAt);
    }
}
