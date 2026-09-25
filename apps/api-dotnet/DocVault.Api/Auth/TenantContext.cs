// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;

namespace DocVault.Api.Auth;

/// <summary>The tenant and department a caller belongs to, derived from their Keycloak groups.</summary>
public sealed record TenantContext(string Tenant, string? Department)
{
    public static readonly TenantContext None = new(string.Empty, null);

    public bool IsResolved => !string.IsNullOrEmpty(Tenant);
}

public static class TenantClaimExtensions
{
    /// <summary>
    /// Derives the caller's tenant from their group membership claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token carries group <em>paths</em>, e.g. <c>["/acme/engineering"]</c>, and the first
    /// segment is the tenant. Keycloak <em>could</em> send the groups' <c>tenant</c> attribute
    /// instead — the built-in User Attribute mapper resolves it from the user's groups — but
    /// that resolution is implicit: an attribute on the user silently overrides the group's,
    /// and across several memberships the first one found wins. The path has no such
    /// ambiguity and carries the department too, so the path is the contract.
    /// </para>
    /// <para>
    /// A user in more than one tenant is rejected rather than guessed at: silently picking the
    /// first would be a cross-tenant data leak waiting to happen.
    /// </para>
    /// </remarks>
    public static TenantContext GetTenantContext(this ClaimsPrincipal principal)
    {
        var paths = principal.FindAll("groups")
            .Select(c => c.Value)
            .Where(v => v.StartsWith('/'))
            .Select(v => v.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(segments => segments.Length > 0)
            .ToList();

        if (paths.Count == 0)
        {
            return TenantContext.None;
        }

        var tenants = paths.Select(p => p[0]).Distinct(StringComparer.Ordinal).ToList();
        if (tenants.Count > 1)
        {
            // Ambiguous. The caller must pick a tenant explicitly (a real product would
            // surface a tenant switcher and mint a token scoped to one).
            throw new MultipleTenantsException(tenants);
        }

        var deepest = paths.OrderByDescending(p => p.Length).First();
        return new TenantContext(tenants[0], deepest.Length > 1 ? deepest[1] : null);
    }
}

public sealed class MultipleTenantsException(IReadOnlyCollection<string> tenants)
    : InvalidOperationException($"Caller belongs to multiple tenants ({string.Join(", ", tenants)}); a tenant-scoped token is required.")
{
    public IReadOnlyCollection<string> Tenants { get; } = tenants;
}
