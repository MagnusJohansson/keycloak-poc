// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Text.Json;
using DocVault.Mcp.Tests.Support;

namespace DocVault.Mcp.Tests.Security;

/// <summary>
/// share_document and unshare_document: what reaches the API, what the assistant is told,
/// and what the audit log keeps. The API decides; these tools only ask it and report.
/// </summary>
public sealed class ShareToolTests(McpServerFactory factory) : IClassFixture<McpServerFactory>
{
    private const string DocumentId = "7b0c8f0e-4d0a-4c46-9f0a-1f2f3a4b5c6d";
    private const string CarolId = "4caf6d80-b180-47e5-9a93-5d6641087cb9";

    private string Token() => factory.Tokens.CreateToken(username: "alice", scope: "openid docvault:read docvault:share");

    [Fact]
    public async Task A_share_reaches_the_API_as_the_user_and_is_recorded_with_who_it_went_to()
    {
        factory.Downstream.Reset();
        factory.Downstream.Api = (HttpStatusCode.Created, $$"""{"type":"user","id":"{{CarolId}}","username":"carol"}""");
        var before = factory.AuditLines().Length;
        try
        {
            var result = await Result(await factory.CallToolAsync(Token(), "share_document", new { id = DocumentId, username = " Carol " }));

            Assert.False(IsError(result), result.ToString());
            var call = Assert.Single(factory.Downstream.ApiRequests);
            Assert.Equal("POST", call.Method);
            Assert.EndsWith($"/documents/{DocumentId}/shares", call.Url);
            Assert.Equal($"Bearer {DownstreamStub.ExchangedToken}", call.Authorization);
            Assert.Equal("Carol", JsonDocument.Parse(call.Body).RootElement.GetProperty("username").GetString());

            var record = Assert.Single(factory.AuditRecords(before));
            Assert.Equal("share_document", record.GetProperty("tool").GetString());
            Assert.Equal("ok", record.GetProperty("outcome").GetString());
            Assert.Equal(DocumentId, record.GetProperty("document.id").GetString());
            Assert.Equal("user:carol", record.GetProperty("share.with").GetString());
        }
        finally
        {
            factory.Downstream.Api = (HttpStatusCode.OK, "{}");
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "No such document, person or department in your tenant.", "not_found")]
    [InlineData(HttpStatusCode.Forbidden, "only a document's owner can share it", "refused")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Nothing was decided", "error")]
    public async Task The_APIs_answer_is_reported_in_words_about_sharing(HttpStatusCode status, string text, string outcome)
    {
        factory.Downstream.Reset();
        factory.Downstream.Api = (status, """{"title":"anything the API said"}""");
        var before = factory.AuditLines().Length;
        try
        {
            var result = await Result(await factory.CallToolAsync(Token(), "share_document", new { id = DocumentId, department = "legal" }));

            Assert.True(IsError(result));
            Assert.Contains(text, Text(result));
            Assert.DoesNotContain("anything the API said", Text(result));
            Assert.Equal(outcome, Assert.Single(factory.AuditRecords(before)).GetProperty("outcome").GetString());
        }
        finally
        {
            factory.Downstream.Api = (HttpStatusCode.OK, "{}");
        }
    }

    [Fact]
    public async Task Unsharing_a_person_looks_up_their_id_and_removes_the_share_by_it()
    {
        factory.Downstream.Reset();
        factory.Downstream.ApiResponder = seen => seen.Method == "GET"
            ? (HttpStatusCode.OK, $$"""{"shares":[{"type":"department","name":"legal"},{"type":"user","id":"{{CarolId}}","username":"carol"}]}""")
            : (HttpStatusCode.NoContent, "");
        try
        {
            var result = await Result(await factory.CallToolAsync(Token(), "unshare_document", new { id = DocumentId, username = "carol" }));

            Assert.False(IsError(result), result.ToString());
            Assert.Equal("No longer shared with that person.", Text(result));
            var delete = Assert.Single(factory.Downstream.ApiRequests, r => r.Method == "DELETE");
            Assert.EndsWith($"/documents/{DocumentId}/shares/users/{CarolId}", delete.Url);
        }
        finally
        {
            factory.Downstream.ApiResponder = null;
        }
    }

    [Fact]
    public async Task Unsharing_someone_it_is_not_shared_with_removes_nothing()
    {
        factory.Downstream.Reset();
        factory.Downstream.ApiResponder = _ => (HttpStatusCode.OK, """{"shares":[{"type":"department","name":"legal"}]}""");
        try
        {
            var result = await Result(await factory.CallToolAsync(Token(), "unshare_document", new { id = DocumentId, username = "carol" }));

            Assert.True(IsError(result));
            Assert.DoesNotContain(factory.Downstream.ApiRequests, r => r.Method == "DELETE");
        }
        finally
        {
            factory.Downstream.ApiResponder = null;
        }
    }

    [Fact]
    public async Task Unsharing_a_department_names_it_in_the_path_escaped()
    {
        factory.Downstream.Reset();
        factory.Downstream.Api = (HttpStatusCode.NoContent, "");
        try
        {
            await factory.CallToolAsync(Token(), "unshare_document", new { id = DocumentId, department = "legal" });

            var delete = Assert.Single(factory.Downstream.ApiRequests);
            Assert.Equal("DELETE", delete.Method);
            Assert.EndsWith($"/documents/{DocumentId}/shares/departments/legal", delete.Url);
        }
        finally
        {
            factory.Downstream.Api = (HttpStatusCode.OK, "{}");
        }
    }

    private static async Task<JsonElement> Result(HttpResponseMessage response) =>
        (await McpServerFactory.ReadMessageAsync(response)).GetProperty("result");

    /// <summary>The SDK leaves isError out of a result that is not one.</summary>
    private static bool IsError(JsonElement result) =>
        result.TryGetProperty("isError", out var isError) && isError.GetBoolean();

    private static string Text(JsonElement result) =>
        result.GetProperty("content")[0].GetProperty("text").GetString()!;
}
