// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace DocVault.Mcp.Tests.Support;

/// <summary>
/// Mints tokens the way Keycloak would for the MCP client, and forged ones a real
/// Keycloak never would. No network: the server under test trusts this key.
/// </summary>
public sealed class TestTokenIssuer : IDisposable
{
    public const string Issuer = "https://test-issuer.local/realms/docvault";
    public const string Resource = "https://mcp.test.local/mcp";
    public const string ApiAudience = "docvault-api";

    private readonly RSA _rsa = RSA.Create(2048);

    public TestTokenIssuer() => SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-key-1" };

    public RsaSecurityKey SigningKey { get; }

    public string CreateToken(
        string username = "alice",
        string? scope = "openid docvault:read",
        string issuer = Issuer,
        string audience = Resource,
        DateTime? expires = null,
        SecurityKey? overrideKey = null)
    {
        var claims = new List<Claim>
        {
            new("sub", $"sub-{username}"),
            new("preferred_username", username),
            new("azp", "docvault-mcp-client"),
        };
        if (scope is not null)
        {
            claims.Add(new Claim("scope", scope));
        }

        var expiry = expires ?? DateTime.UtcNow.AddMinutes(5);
        var notBefore = expiry < DateTime.UtcNow ? expiry.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(-1);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore,
            expires: expiry,
            signingCredentials: new SigningCredentials(overrideKey ?? SigningKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>The canonical JWT attack: no signature, alg "none".</summary>
    public static string CreateUnsignedToken()
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = B64("""{"alg":"none","typ":"JWT"}""");
        var payload = B64(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sub"] = "sub-attacker",
            ["preferred_username"] = "attacker",
            ["iss"] = Issuer,
            ["aud"] = Resource,
            ["scope"] = "docvault:read docvault:write docvault:delete",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        }));
        return $"{header}.{payload}.";
    }

    public void Dispose() => _rsa.Dispose();
}
