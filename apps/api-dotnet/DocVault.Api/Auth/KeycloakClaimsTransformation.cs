// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace DocVault.Api.Auth;

/// <summary>
/// Flattens Keycloak's nested role claims into standard <see cref="ClaimTypes.Role"/> claims
/// so that <c>[Authorize(Roles = ...)]</c> and <c>RequireRole(...)</c> work at all.
/// </summary>
/// <remarks>
/// <para>
/// This class exists because Keycloak does not emit roles in a shape ASP.NET Core understands.
/// A Keycloak access token looks like this:
/// </para>
/// <code>
/// {
///   "realm_access":    { "roles": ["platform-admin", "default-roles-docvault"] },
///   "resource_access": { "docvault-api": { "roles": ["doc.editor"] } }
/// }
/// </code>
/// <para>
/// ASP.NET Core looks for flat <c>role</c> claims. Without this transformation every
/// <c>RequireRole</c> policy silently denies, producing a 403 that looks like a
/// configuration bug on the Keycloak side. It is the single most common failure when
/// wiring Keycloak to .NET.
/// </para>
/// <para>
/// Only roles for <em>this</em> API's client are imported. Importing every client's roles
/// would let a role named <c>doc.admin</c> on some unrelated client grant access here.
/// </para>
/// </remarks>
public sealed class KeycloakClaimsTransformation(
    IConfiguration configuration,
    ILogger<KeycloakClaimsTransformation> logger) : IClaimsTransformation
{
    /// <summary>Marks a principal as already transformed. See the idempotency note below.</summary>
    private const string TransformedMarker = "docvault:roles-transformed";

    private readonly string _apiClientId =
        configuration["Keycloak:Audience"]
        ?? throw new InvalidOperationException("Keycloak:Audience must be configured.");

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return Task.FromResult(principal);
        }

        // IClaimsTransformation runs on EVERY request, and in some hosting configurations
        // more than once for the same principal instance. Without this guard the role
        // claims accumulate duplicates on each pass, which is invisible in behaviour but
        // grows the principal unboundedly on long-lived connections (e.g. SignalR).
        if (identity.HasClaim(TransformedMarker, "true"))
        {
            return Task.FromResult(principal);
        }

        identity.AddClaim(new Claim(TransformedMarker, "true"));

        AddRealmRoles(identity);
        AddClientRoles(identity);

        return Task.FromResult(principal);
    }

    /// <summary>Imports <c>realm_access.roles</c> — roles that apply across every client.</summary>
    private void AddRealmRoles(ClaimsIdentity identity)
    {
        var realmAccess = identity.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(realmAccess))
        {
            return;
        }

        foreach (var role in ReadRoles(realmAccess, "realm_access"))
        {
            // Keycloak grants every user a `default-roles-<realm>` composite. It carries no
            // application meaning and only clutters the principal, so drop it.
            if (role.StartsWith("default-roles-", StringComparison.Ordinal))
            {
                continue;
            }

            AddRoleIfMissing(identity, role);
        }
    }

    /// <summary>Imports <c>resource_access["docvault-api"].roles</c> — roles scoped to this API.</summary>
    private void AddClientRoles(ClaimsIdentity identity)
    {
        var resourceAccess = identity.FindFirst("resource_access")?.Value;
        if (string.IsNullOrEmpty(resourceAccess))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(resourceAccess);
            if (!document.RootElement.TryGetProperty(_apiClientId, out var client))
            {
                // Normal for a token minted for a different audience. The audience check in
                // JwtBearer will already have rejected it if that mattered.
                return;
            }

            if (!client.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var role in roles.EnumerateArray())
            {
                if (role.GetString() is { Length: > 0 } value)
                {
                    AddRoleIfMissing(identity, value);
                }
            }
        }
        catch (JsonException ex)
        {
            // A malformed claim means a broken token, not a broken user. Log loudly and
            // grant nothing — never fall back to "allow" on a parsing failure.
            logger.LogWarning(ex, "Could not parse resource_access claim; no client roles granted.");
        }
    }

    private static IEnumerable<string> ReadRoles(string json, string claimName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var role in roles.EnumerateArray())
            {
                if (role.GetString() is { Length: > 0 } value)
                {
                    yield return value;
                }
            }
        }
    }

    private static void AddRoleIfMissing(ClaimsIdentity identity, string role)
    {
        if (!identity.HasClaim(identity.RoleClaimType, role))
        {
            identity.AddClaim(new Claim(identity.RoleClaimType, role));
        }
    }
}
