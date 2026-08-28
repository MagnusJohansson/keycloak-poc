// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Text.Json;
using DocVault.Desktop.Auth;

namespace DocVault.Desktop.Auth.Tests;

/// <summary>
/// These deliberately mirror <c>KeycloakClaimsTransformationTests</c> in the API test suite.
/// If the client and the server ever disagree about what "having a role" means, the UI shows a
/// button the API then refuses — so both sides assert the same rules.
/// </summary>
public class TokenClaimsTests
{
    /// <summary>Builds an unsigned JWT. Only the payload matters; nothing here validates signatures.</summary>
    private static string TokenWith(string payloadJson)
    {
        static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"none"}""")}.{B64(payloadJson)}.";
    }

    private static JsonElement? Payload(string json) => TokenClaims.DecodePayload(TokenWith(json));

    [Fact]
    public void Merges_realm_roles_and_this_apis_client_roles()
    {
        var roles = TokenClaims.Roles(Payload("""
            {"realm_access":{"roles":["platform-admin"]},
             "resource_access":{"docvault-api":{"roles":["doc.editor"]}}}
            """));

        Assert.Contains("platform-admin", roles);
        Assert.Contains("doc.editor", roles);
    }

    [Fact]
    public void Ignores_roles_granted_on_a_different_client()
    {
        // Otherwise a doc.admin role on some unrelated client would appear to grant access here.
        var roles = TokenClaims.Roles(Payload("""
            {"resource_access":{"some-other-app":{"roles":["doc.admin"]}}}
            """));

        Assert.Empty(roles);
    }

    [Fact]
    public void Drops_the_default_roles_composite()
    {
        var roles = TokenClaims.Roles(Payload("""
            {"realm_access":{"roles":["default-roles-docvault","platform-admin"]}}
            """));

        Assert.Equal(["platform-admin"], roles);
    }

    [Fact]
    public void Derives_tenant_from_the_group_path()
    {
        Assert.Equal("acme", TokenClaims.Tenant(Payload("""{"groups":["/acme/engineering"]}""")));
        Assert.Equal("globex", TokenClaims.Tenant(Payload("""{"groups":["/globex"]}""")));
    }

    [Fact]
    public void Returns_no_tenant_when_the_user_has_no_groups()
    {
        Assert.Null(TokenClaims.Tenant(Payload("""{"groups":[]}""")));
        Assert.Null(TokenClaims.Tenant(Payload("""{}""")));
    }

    [Fact]
    public void Reads_acr_and_username()
    {
        var payload = Payload("""{"acr":"silver","preferred_username":"carol"}""");

        Assert.Equal("silver", TokenClaims.Acr(payload));
        Assert.Equal("carol", TokenClaims.Username(payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    public void Malformed_tokens_yield_nothing_rather_than_throwing(string? token)
    {
        // A broken token must never crash the UI, and must never be read as granting anything.
        var payload = TokenClaims.DecodePayload(token);
        Assert.Empty(TokenClaims.Roles(payload));
    }
}
