// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Collections.Concurrent;

namespace DocVault.Api.Domain;

public sealed record Document(
    Guid Id,
    string Tenant,
    string Title,
    string OwnerUsername,
    bool IsClassified,
    DateTimeOffset CreatedAt);

public interface IDocumentStore
{
    IReadOnlyList<Document> ListForTenant(string tenant, bool includeClassified);
    Document? Get(Guid id);
    Document Add(string tenant, string title, string ownerUsername, bool isClassified);
    bool Delete(Guid id);
}

/// <summary>
/// Deliberately in-memory. This lab is about identity, not persistence — a database would
/// add setup cost without teaching anything about Keycloak.
/// </summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<Guid, Document> _documents = new();

    public InMemoryDocumentStore()
    {
        // Seeded so tenant isolation is visible immediately: alice (acme) and bob (globex)
        // see disjoint lists using the exact same endpoint and the exact same code path.
        Add("acme", "Acme Q3 Roadmap", "alice", isClassified: false);
        Add("acme", "Acme Engineering Handbook", "alice", isClassified: false);
        Add("acme", "Acme Acquisition Memo", "carol", isClassified: true);
        Add("globex", "Globex Supplier List", "bob", isClassified: false);
    }

    public IReadOnlyList<Document> ListForTenant(string tenant, bool includeClassified) =>
        _documents.Values
            .Where(d => d.Tenant == tenant && (includeClassified || !d.IsClassified))
            .OrderBy(d => d.CreatedAt)
            .ToList();

    public Document? Get(Guid id) => _documents.GetValueOrDefault(id);

    public Document Add(string tenant, string title, string ownerUsername, bool isClassified)
    {
        var document = new Document(Guid.NewGuid(), tenant, title, ownerUsername, isClassified, DateTimeOffset.UtcNow);
        _documents[document.Id] = document;
        return document;
    }

    public bool Delete(Guid id) => _documents.TryRemove(id, out _);
}
