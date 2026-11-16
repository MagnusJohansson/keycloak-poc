// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Security.Cryptography;
using DocVault.Mcp.Tests.Support;
using Microsoft.IdentityModel.Tokens;

namespace DocVault.Mcp.Tests.Security;

/// <summary>
/// Adversarial tests. Each forges a token a real Keycloak would never issue for this
/// server and asserts it is refused before any tool runs or any downstream call is made.
/// </summary>
public sealed class TokenValidationTests(McpServerFactory factory) : IClassFixture<McpServerFactory>
{
    [Fact]
    public async Task Accepts_a_well_formed_token()
    {
        // Control case. If this fails, every rejection below is meaningless.
        var response = await factory.ListToolsAsync(factory.Tokens.CreateToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Challenges_a_request_without_a_token_and_points_at_the_metadata()
    {
        var response = await factory.ListToolsAsync(token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // RFC 9728: this is how an MCP client finds the authorization server.
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("Bearer", challenge);
        Assert.Contains("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"", challenge);
    }

    [Fact]
    public async Task Rejects_a_token_issued_for_the_DocVault_API()
    {
        // Valid signature, issuer and lifetime; wrong audience. This is the token a
        // client holds for the API, replayed at the MCP server.
        var token = factory.Tokens.CreateToken(audience: TestTokenIssuer.ApiAudience);

        await AssertRefusedAsync(token);
    }

    [Fact]
    public async Task Rejects_a_token_issued_for_another_MCP_server()
    {
        // Same realm, same client, a sibling resource. Audience binding is what stops
        // one MCP server's token working at another.
        var token = factory.Tokens.CreateToken(audience: "https://other.test.local/mcp");

        await AssertRefusedAsync(token);
    }

    [Theory]
    [InlineData("https://mcp.test.local/mcp/")] // trailing slash
    [InlineData("https://mcp.test.local")] // the origin, without the path
    [InlineData("https://MCP.test.local/mcp")] // different case
    [InlineData("docvault-mcp-client")] // the calling client's id
    public async Task Rejects_an_audience_that_only_resembles_this_server(string audience)
    {
        // The comparison is exact. Nothing is normalised into matching.
        await AssertRefusedAsync(factory.Tokens.CreateToken(audience: audience));
    }

    [Fact]
    public async Task Rejects_an_unsigned_alg_none_token()
    {
        await AssertRefusedAsync(TestTokenIssuer.CreateUnsignedToken());
    }

    [Fact]
    public async Task Rejects_a_token_signed_with_another_key()
    {
        using var rsa = RSA.Create(2048);
        var token = factory.Tokens.CreateToken(overrideKey: new RsaSecurityKey(rsa) { KeyId = "attacker" });

        await AssertRefusedAsync(token);
    }

    [Fact]
    public async Task Rejects_a_token_from_another_realm()
    {
        var token = factory.Tokens.CreateToken(issuer: "https://test-issuer.local/realms/other");

        await AssertRefusedAsync(token);
    }

    [Fact]
    public async Task Rejects_an_expired_token()
    {
        var token = factory.Tokens.CreateToken(expires: DateTime.UtcNow.AddMinutes(-10));

        await AssertRefusedAsync(token);
    }

    private async Task AssertRefusedAsync(string token)
    {
        factory.Downstream.Reset();

        // A tool call, not just tools/list: the refusal must come before the tool.
        var response = await factory.CallToolAsync(token, "list_documents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Downstream.Requests);
    }
}
