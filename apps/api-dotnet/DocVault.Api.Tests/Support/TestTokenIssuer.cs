using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace DocVault.Api.Tests.Support;

/// <summary>
/// Mints tokens that look exactly like Keycloak's, signed by a key this test process
/// controls. Lets the security suite forge the malformed tokens a real Keycloak would
/// never issue — expired, wrong audience, wrong issuer, wrong signing key, alg=none.
/// </summary>
public sealed class TestTokenIssuer : IDisposable
{
    public const string DefaultIssuer = "https://test-issuer.local/realms/docvault";
    public const string DefaultAudience = "docvault-api";

    private readonly RSA _rsa = RSA.Create(2048);

    public TestTokenIssuer()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-key-1" };
    }

    public RsaSecurityKey SigningKey { get; }

    public string CreateToken(
        string username = "alice",
        string[]? realmRoles = null,
        string[]? apiRoles = null,
        string[]? groups = null,
        string? acr = null,
        string? scope = "openid profile email",
        string issuer = DefaultIssuer,
        string audience = DefaultAudience,
        DateTime? expires = null,
        SecurityKey? overrideKey = null)
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new("preferred_username", username),
            new("email", $"{username}@acme.test"),
        };

        if (scope is not null)
        {
            claims.Add(new Claim("scope", scope));
        }

        if (acr is not null)
        {
            claims.Add(new Claim("acr", acr));
        }

        // Keycloak emits these as nested JSON objects, not as repeated flat claims.
        // Reproducing that shape exactly is the whole point — it is what
        // KeycloakClaimsTransformation has to cope with.
        if (realmRoles is { Length: > 0 })
        {
            claims.Add(new Claim(
                "realm_access",
                JsonSerializer.Serialize(new { roles = realmRoles }),
                Microsoft.IdentityModel.JsonWebTokens.JsonClaimValueTypes.Json));
        }

        if (apiRoles is { Length: > 0 })
        {
            claims.Add(new Claim(
                "resource_access",
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [DefaultAudience] = new { roles = apiRoles },
                }),
                Microsoft.IdentityModel.JsonWebTokens.JsonClaimValueTypes.Json));
        }

        foreach (var group in groups ?? [])
        {
            claims.Add(new Claim("groups", group));
        }

        var credentials = new SigningCredentials(
            overrideKey ?? SigningKey,
            SecurityAlgorithms.RsaSha256);

        var expiry = expires ?? DateTime.UtcNow.AddMinutes(5);

        // notBefore must precede expiry, including when a test deliberately asks for an
        // ALREADY-EXPIRED token. Anchoring it to `now` would make such a token
        // unconstructable rather than merely invalid; anchoring it to expiry would push it
        // into the future for ordinary tokens, making them not-yet-valid. Take the earlier.
        var notBefore = expiry < DateTime.UtcNow
            ? expiry.AddMinutes(-1)
            : DateTime.UtcNow.AddMinutes(-1);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore,
            expires: expiry,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Forges an unsigned token — the classic <c>alg: none</c> attack.</summary>
    public static string CreateUnsignedToken(string username = "attacker")
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = B64("""{"alg":"none","typ":"JWT"}""");
        var payload = B64(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["preferred_username"] = username,
            ["iss"] = DefaultIssuer,
            ["aud"] = DefaultAudience,
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            ["resource_access"] = new Dictionary<string, object>
            {
                [DefaultAudience] = new Dictionary<string, object> { ["roles"] = new[] { "doc.admin" } },
            },
        }));

        return $"{header}.{payload}.";
    }

    public void Dispose() => _rsa.Dispose();
}
