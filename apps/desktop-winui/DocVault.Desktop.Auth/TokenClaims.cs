using System.Text.Json;

namespace DocVault.Desktop.Auth;

/// <summary>
/// Reads the claims a Keycloak access token carries, for display and for deciding which UI to show.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors <c>KeycloakClaimsTransformation</c> in the API (see
/// <c>apps/api-dotnet/DocVault.Api/Auth/</c>) on purpose: the client and the server must agree on
/// what "having a role" means, or the UI shows a button the API then refuses.
/// </para>
/// <para>
/// It is a <b>usability</b> control, never a security one. Anything decided here runs on the
/// user's machine and can be changed by them. The API is the only place authorization is
/// enforced — which the test suite proves by calling endpoints directly with under-privileged
/// tokens.
/// </para>
/// </remarks>
public static class TokenClaims
{
    /// <summary>Decodes a JWT payload. Does NOT validate the signature — the API does that.</summary>
    public static JsonElement? DecodePayload(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The caller's effective roles: realm roles plus this API's client roles.
    /// </summary>
    /// <remarks>
    /// Roles belonging to <em>other</em> clients are ignored, exactly as the API ignores them.
    /// Importing them would mean a <c>doc.admin</c> granted on some unrelated client appeared to
    /// grant access here. The <c>default-roles-*</c> composite Keycloak gives every user carries
    /// no application meaning and is dropped.
    /// </remarks>
    public static IReadOnlyList<string> Roles(JsonElement? payload, string apiClientId = "docvault-api")
    {
        if (payload is not { } claims)
        {
            return [];
        }

        var roles = new List<string>();

        if (claims.TryGetProperty("realm_access", out var realm))
        {
            roles.AddRange(ReadRoles(realm));
        }

        if (claims.TryGetProperty("resource_access", out var resource)
            && resource.TryGetProperty(apiClientId, out var client))
        {
            roles.AddRange(ReadRoles(client));
        }

        return roles
            .Where(r => !r.StartsWith("default-roles-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The caller's tenant, taken from the first segment of their group path.
    /// </summary>
    /// <remarks>
    /// The token carries group <em>paths</em> such as <c>/acme/engineering</c>, because Keycloak
    /// has no built-in mapper that copies group <em>attributes</em> into a token. The path is the
    /// contract, and the API parses it the same way.
    /// </remarks>
    public static string? Tenant(JsonElement? payload)
    {
        if (payload is not { } claims || !claims.TryGetProperty("groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return groups.EnumerateArray()
            .Select(g => g.GetString())
            .Where(g => !string.IsNullOrEmpty(g))
            .Select(g => g!.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(segments => segments.Length > 0)
            .Select(segments => segments[0])
            .FirstOrDefault();
    }

    /// <summary>The authentication level reached: "bronze" (password) or "silver" (password + OTP).</summary>
    public static string? Acr(JsonElement? payload) =>
        payload is { } claims && claims.TryGetProperty("acr", out var acr) ? acr.GetString() : null;

    public static string? Username(JsonElement? payload) =>
        payload is { } claims && claims.TryGetProperty("preferred_username", out var name)
            ? name.GetString()
            : null;

    /// <summary>Pretty-prints the payload for the claims panel.</summary>
    public static string Pretty(JsonElement? payload) =>
        payload is { } claims
            ? JsonSerializer.Serialize(claims, new JsonSerializerOptions { WriteIndented = true })
            : "(no token)";

    private static IEnumerable<string> ReadRoles(JsonElement container)
    {
        if (!container.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
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
