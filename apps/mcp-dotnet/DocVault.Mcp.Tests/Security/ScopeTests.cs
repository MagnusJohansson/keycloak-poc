// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using DocVault.Mcp.Tests.Support;

namespace DocVault.Mcp.Tests.Security;

/// <summary>
/// Scopes say what the assistant may do. A call without the scope gets the HTTP
/// challenge MCP specifies, which names the scope, so a client can ask the user for
/// it. 403, not 401: the token is fine, the permission is missing.
/// </summary>
public sealed class ScopeTests(McpServerFactory factory) : IClassFixture<McpServerFactory>
{
    [Theory]
    [InlineData("list_documents", "openid", "docvault:read")]
    [InlineData("create_document", "openid docvault:read", "docvault:write")]
    [InlineData("delete_document", "openid docvault:read docvault:write", "docvault:delete")]
    public async Task A_call_without_the_scope_gets_an_insufficient_scope_challenge(
        string tool, string granted, string required)
    {
        factory.Downstream.Reset();
        var token = factory.Tokens.CreateToken(scope: granted);

        var response = await factory.CallToolAsync(token, tool, new { title = "x", id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("error=\"insufficient_scope\"", challenge);
        Assert.Contains($"scope=\"{required}\"", challenge);
        Assert.Contains("resource_metadata=\"https://mcp.test.local/.well-known/oauth-protected-resource/mcp\"", challenge);
        // Refused before the tool: no exchange, no API call.
        Assert.Empty(factory.Downstream.Requests);
    }

    [Fact]
    public async Task Scopes_have_no_hierarchy()
    {
        // docvault:delete alone does not let the assistant read or write.
        var token = factory.Tokens.CreateToken(scope: "docvault:delete");

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CallToolAsync(token, "list_documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CallToolAsync(token, "create_document", new { title = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_token_with_no_scope_claim_can_call_nothing()
    {
        var token = factory.Tokens.CreateToken(scope: null);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CallToolAsync(token, "list_documents")).StatusCode);
    }

    [Fact]
    public async Task A_scope_is_matched_whole_not_by_prefix()
    {
        var token = factory.Tokens.CreateToken(scope: "docvault:reader xdocvault:read");

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CallToolAsync(token, "list_documents")).StatusCode);
    }

    [Fact]
    public async Task Every_tool_is_listed_whatever_the_scope()
    {
        // The assistant has to see a tool to ask for the permission to use it.
        var response = await factory.ListToolsAsync(factory.Tokens.CreateToken(scope: "openid docvault:read"));
        var message = await McpServerFactory.ReadMessageAsync(response);

        var names = message.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).Order().ToArray();
        Assert.Equal(["create_document", "delete_document", "list_documents"], names);
    }

    [Fact]
    public async Task Only_delete_is_marked_destructive_and_only_list_read_only()
    {
        var response = await factory.ListToolsAsync(factory.Tokens.CreateToken());
        var tools = (await McpServerFactory.ReadMessageAsync(response)).GetProperty("result").GetProperty("tools")
            .EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("annotations"));

        Assert.True(tools["list_documents"].GetProperty("readOnlyHint").GetBoolean());
        Assert.False(tools["create_document"].GetProperty("readOnlyHint").GetBoolean());
        Assert.False(tools["create_document"].GetProperty("destructiveHint").GetBoolean());
        Assert.True(tools["delete_document"].GetProperty("destructiveHint").GetBoolean());
    }

    [Fact]
    public async Task A_batch_is_refused_if_any_call_in_it_lacks_its_scope()
    {
        factory.Downstream.Reset();
        var token = factory.Tokens.CreateToken(scope: "openid docvault:read");
        var batch = new object[]
        {
            new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "list_documents", arguments = new { } } },
            new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "delete_document", arguments = new { id = Guid.NewGuid().ToString() } } },
        };

        var response = await factory.PostAsync(token, batch);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("scope=\"docvault:delete\"", response.Headers.WwwAuthenticate.ToString());
        Assert.Empty(factory.Downstream.Requests);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_rather_than_passed_through_unchecked()
    {
        var token = factory.Tokens.CreateToken(scope: "openid docvault:read");

        var response = await factory.CallToolAsync(token, "create_document", new { title = new string('a', 70_000) });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
