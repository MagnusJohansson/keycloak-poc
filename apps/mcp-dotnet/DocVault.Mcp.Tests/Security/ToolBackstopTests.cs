// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using DocVault.Mcp.Audit;
using DocVault.Mcp.Downstream;
using DocVault.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;

namespace DocVault.Mcp.Tests.Security;

/// <summary>
/// The scope challenge is middleware. If a request ever gets past it (a path or a
/// body shape it does not cover), the tool itself must still refuse. These call
/// the tools directly, with no middleware at all.
/// </summary>
public sealed class ToolBackstopTests
{
    private sealed class NeverCalled : ITokenExchanger
    {
        public int Calls;

        public Task<string> ExchangeForApiAsync(string subjectToken, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult("exchanged");
        }
    }

    private sealed class Recorded : IAuditLog
    {
        public List<(string Event, string Outcome, Dictionary<string, object?> Fields)> Records { get; } = [];

        public void Record(string eventName, string outcome, AuditCaller? caller, params (string Key, object? Value)[] fields) =>
            Records.Add((eventName, outcome, fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value)));
    }

    private static (DocumentTools Tools, NeverCalled Exchanger, Recorded Audit) ToolsFor(string scope)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", scope)], "test")),
        };
        context.Request.Headers.Authorization = "Bearer incoming";
        var exchanger = new NeverCalled();
        var api = new DocVaultApiClient(new HttpClient { BaseAddress = new Uri("https://api.test.local") }, exchanger);
        var audit = new Recorded();
        return (new DocumentTools(new HttpContextAccessor { HttpContext = context }, api, audit), exchanger, audit);
    }

    [Fact]
    public async Task A_tool_refuses_a_caller_without_its_scope_even_with_no_middleware()
    {
        var (tools, exchanger, audit) = ToolsFor("openid docvault:read");

        await Assert.ThrowsAsync<McpException>(() => tools.DeleteDocument(Guid.NewGuid().ToString(), CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => tools.CreateDocument("title", CancellationToken.None));

        // Refused before the exchange: nothing left this process.
        Assert.Equal(0, exchanger.Calls);

        // And the refusal is on record: this is the case where the middleware was bypassed.
        Assert.Equal(2, audit.Records.Count);
        Assert.All(audit.Records, r =>
        {
            Assert.Equal(AuditEvents.ToolCall, r.Event);
            Assert.Equal(AuditOutcomes.Refused, r.Outcome);
            Assert.Equal("missing_scope", r.Fields["reason"]);
        });
        Assert.Equal("docvault:delete", audit.Records[0].Fields["scope.required"]);
        Assert.Equal("delete_document", audit.Records[0].Fields["tool"]);
    }

    [Fact]
    public async Task A_tool_refuses_a_caller_with_no_scopes()
    {
        var (tools, exchanger, _) = ToolsFor(string.Empty);

        await Assert.ThrowsAsync<McpException>(() => tools.ListDocuments(CancellationToken.None));
        Assert.Equal(0, exchanger.Calls);
    }
}
