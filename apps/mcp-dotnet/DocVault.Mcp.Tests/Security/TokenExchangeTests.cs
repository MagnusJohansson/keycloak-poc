// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Web;
using DocVault.Mcp.Tests.Support;

namespace DocVault.Mcp.Tests.Security;

/// <summary>
/// The token a client presents here is for this server only. What reaches the
/// DocVault API is a token exchanged for it (RFC 8693), never the incoming one.
/// </summary>
public sealed class TokenExchangeTests(McpServerFactory factory) : IClassFixture<McpServerFactory>
{
    [Fact]
    public async Task The_API_receives_the_exchanged_token_never_the_incoming_one()
    {
        factory.Downstream.Reset();
        var incoming = factory.Tokens.CreateToken();

        var response = await factory.CallToolAsync(incoming, "list_documents");
        var message = await McpServerFactory.ReadMessageAsync(response);

        Assert.Contains("Acme Q3 Roadmap", message.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        var api = Assert.Single(factory.Downstream.ApiRequests);
        Assert.Equal("https://api.test.local/documents", api.Url);
        Assert.Equal($"Bearer {DownstreamStub.ExchangedToken}", api.Authorization);
        Assert.DoesNotContain(incoming, api.Authorization);
    }

    [Fact]
    public async Task The_exchange_names_the_API_as_audience_and_authenticates_as_this_server()
    {
        factory.Downstream.Reset();
        var incoming = factory.Tokens.CreateToken();

        await factory.CallToolAsync(incoming, "list_documents");

        var exchange = Assert.Single(factory.Downstream.TokenRequests);
        Assert.Equal($"{TestTokenIssuer.Issuer}/protocol/openid-connect/token", exchange.Url);
        var form = HttpUtility.ParseQueryString(exchange.Body);
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", form["grant_type"]);
        Assert.Equal(incoming, form["subject_token"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", form["subject_token_type"]);
        Assert.Equal(TestTokenIssuer.ApiAudience, form["audience"]);
        // The server's client_id is its resource URL (see mcp.tf).
        Assert.Equal(TestTokenIssuer.Resource, form["client_id"]);
        Assert.Equal(McpServerFactory.ClientSecret, form["client_secret"]);
    }

    [Fact]
    public async Task A_refused_exchange_is_a_tool_error_that_does_not_leak_Keycloaks_answer()
    {
        factory.Downstream.Reset();
        factory.Downstream.Exchange = (HttpStatusCode.Forbidden, """{"error":"access_denied","error_description":"Client is not within the token audience"}""");
        try
        {
            var response = await factory.CallToolAsync(factory.Tokens.CreateToken(), "list_documents");
            var result = (await McpServerFactory.ReadMessageAsync(response)).GetProperty("result");

            Assert.True(result.GetProperty("isError").GetBoolean());
            var text = result.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.DoesNotContain("token audience", text);
            Assert.DoesNotContain("access_denied", text);
            // No API call without an exchanged token.
            Assert.Empty(factory.Downstream.ApiRequests);
        }
        finally
        {
            factory.Downstream.Exchange = (HttpStatusCode.OK, $$"""{"access_token":"{{DownstreamStub.ExchangedToken}}"}""");
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "your DocVault role")] // dave: signed in, no role
    [InlineData(HttpStatusCode.NotFound, "No such document in your tenant")] // another tenant's document
    public async Task The_APIs_refusal_becomes_a_tool_error_the_assistant_can_explain(HttpStatusCode status, string expected)
    {
        factory.Downstream.Reset();
        factory.Downstream.Api = (status, "");
        try
        {
            var token = factory.Tokens.CreateToken(scope: "docvault:read docvault:delete");
            var response = await factory.CallToolAsync(token, "delete_document", new { id = Guid.NewGuid().ToString() });

            // The MCP call itself succeeded; the tool reports the refusal.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await McpServerFactory.ReadMessageAsync(response)).GetProperty("result");
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Contains(expected, result.GetProperty("content")[0].GetProperty("text").GetString());
        }
        finally
        {
            factory.Downstream.Api = (HttpStatusCode.OK, "{}");
        }
    }

    [Theory]
    [InlineData("create_document", """{"title":""}""")]
    [InlineData("create_document", """{"title":"line one\nline two"}""")]
    [InlineData("create_document", """{"title":"invoice\u202Egnp.exe"}""")] // right-to-left override
    [InlineData("create_document", """{"title":"zero\u200Bwidth"}""")]
    [InlineData("create_document", """{"title":"tag\uDB40\uDC41chars"}""")] // U+E0041, outside the BMP
    [InlineData("delete_document", """{"id":"not-a-guid"}""")]
    [InlineData("delete_document", """{"id":"../documents"}""")]
    [InlineData("share_document", """{"id":"not-a-guid","username":"carol"}""")]
    [InlineData("share_document", """{"id":"7b0c8f0e-4d0a-4c46-9f0a-1f2f3a4b5c6d"}""")]
    [InlineData("share_document", """{"id":"7b0c8f0e-4d0a-4c46-9f0a-1f2f3a4b5c6d","username":"carol","department":"legal"}""")]
    [InlineData("share_document", """{"id":"7b0c8f0e-4d0a-4c46-9f0a-1f2f3a4b5c6d","username":"car​ol"}""")]
    [InlineData("share_document", """{"id":"7b0c8f0e-4d0a-4c46-9f0a-1f2f3a4b5c6d","username":"  "}""")]
    [InlineData("share_document", """{"id":"7b0c8f0e-4d0a-4c46-9f0a-1f2f3a4b5c6d","department":"le#gal"}""")]
    [InlineData("share_document", """{"id":"7b0c8f0e-4d0a-4c46-9f0a-1f2f3a4b5c6d","department":"../admin"}""")]
    [InlineData("unshare_document", """{"id":"7b0c8f0e-4d0a-4c46-9f0a-1f2f3a4b5c6d"}""")]
    public async Task Bad_arguments_are_refused_before_any_downstream_call(string tool, string arguments)
    {
        factory.Downstream.Reset();
        var token = factory.Tokens.CreateToken(scope: "docvault:read docvault:write docvault:delete docvault:share");

        var response = await factory.CallToolAsync(token, tool, System.Text.Json.JsonDocument.Parse(arguments).RootElement);
        var result = (await McpServerFactory.ReadMessageAsync(response)).GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Empty(factory.Downstream.Requests);
    }
}
