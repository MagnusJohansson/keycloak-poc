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
    /// The token carries group <em>paths</em>, e.g. <c>["/acme/engineering"]</c>, because
    /// Keycloak has no built-in protocol mapper that copies group <em>attributes</em> into a
    /// token. The <c>tenant</c> attribute set on the group in Terraform is admin-side metadata
    /// only — it never reaches the client. So the path is the contract, and the first segment
    /// is the tenant.
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
