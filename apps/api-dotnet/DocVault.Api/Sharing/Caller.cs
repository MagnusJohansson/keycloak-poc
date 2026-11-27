// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using System.Text.RegularExpressions;
using DocVault.Api.Auth;

namespace DocVault.Api.Sharing;

/// <summary>One relationship, as OpenFGA writes it: <c>user</c> is <c>relation</c> of <c>object</c>.</summary>
public sealed record TupleKey(string User, string Relation, string Object);

/// <summary>How DocVault's things are named in OpenFGA (infra/openfga/docvault.fga).</summary>
public static partial class FgaNames
{
    public static string User(Guid id) => $"user:{id:D}";

    public static string Tenant(string tenant) => $"tenant:{tenant}";

    public static string TenantMembers(string tenant) => $"tenant:{tenant}#member";

    public static string Department(string tenant, string department) => $"department:{tenant}/{department}";

    public static string DepartmentMembers(string tenant, string department) => $"{Department(tenant, department)}#member";

    public static string Document(Guid id) => $"document:{id:D}";

    /// <summary>
    /// A tenant or department name that can become part of an OpenFGA id. Group names
    /// are Keycloak's to choose and could hold ':' or '#', which mean something in a
    /// tuple. A name that does not fit is left out: that membership grants nothing,
    /// which is the safe way to be wrong.
    /// Starts with a letter or digit: "." and ".." are path segments to anything that
    /// builds a URL from a name (Keycloak's group-by-path collapsed "acme/." to "/acme").
    /// \z, not $: in .NET, $ also matches before a final newline.
    /// </summary>
    public static bool IsSafeName(string name) => SafeName().IsMatch(name);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex SafeName();
}

/// <summary>
/// The caller as OpenFGA sees them: their user id, and the memberships to send with
/// every check as contextual tuples.
/// </summary>
/// <remarks>
/// <para>
/// OpenFGA believes contextual tuples completely: a check that says bob is a member of
/// acme lets bob in, whatever is stored (measured on v1.22.1, and stated in
/// infra/openfga/docvault.fga.yml). So this is the ONLY place they are made, and they
/// are made from the validated token alone: <c>sub</c> and the <c>groups</c> paths,
/// through the same <see cref="TenantClaimExtensions.GetTenantContext"/> every other
/// endpoint uses. Nothing a request carries can add one.
/// </para>
/// <para>
/// Membership is not stored in OpenFGA, so there is no second copy of it to go stale:
/// someone who leaves /acme/legal loses that department's shares with their next token.
/// </para>
/// </remarks>
public sealed record Caller(Guid UserId, string Tenant, IReadOnlyList<TupleKey> Memberships)
{
    public string User => FgaNames.User(UserId);

    /// <summary>
    /// Null when the token cannot be anyone in OpenFGA: no <c>sub</c> that is a Keycloak
    /// user id, or no tenant. Such a caller sees nothing, not even their own documents.
    /// Throws <see cref="MultipleTenantsException"/> as <c>GetTenantContext</c> does.
    /// </summary>
    public static Caller? From(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var userId))
        {
            return null;
        }

        var tenant = principal.GetTenantContext();
        if (!tenant.IsResolved || !FgaNames.IsSafeName(tenant.Tenant))
        {
            return null;
        }

        var user = FgaNames.User(userId);
        var memberships = new List<TupleKey> { new(user, "member", FgaNames.Tenant(tenant.Tenant)) };

        // Every department of the tenant the token names, not only the deepest one that
        // TenantContext.Department reports: someone can be in two.
        // Paths only, as in GetTenantContext: a value without the leading slash is not one.
        var departments = principal.FindAll("groups")
            .Where(c => c.Value.StartsWith('/'))
            .Select(c => c.Value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(segments => segments.Length >= 2 && segments[0] == tenant.Tenant && FgaNames.IsSafeName(segments[1]))
            .Select(segments => segments[1])
            .Distinct(StringComparer.Ordinal);

        memberships.AddRange(departments.Select(d => new TupleKey(user, "member", FgaNames.Department(tenant.Tenant, d))));
        return new Caller(userId, tenant.Tenant, memberships);
    }
}
