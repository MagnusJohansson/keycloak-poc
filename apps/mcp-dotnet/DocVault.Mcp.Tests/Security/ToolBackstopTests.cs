using System.Security.Claims;
using DocVault.Mcp.Downstream;
using DocVault.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
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

    private static (DocumentTools Tools, NeverCalled Exchanger) ToolsFor(string scope)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", scope)], "test")),
        };
        context.Request.Headers.Authorization = "Bearer incoming";
        var exchanger = new NeverCalled();
        var api = new DocVaultApiClient(new HttpClient { BaseAddress = new Uri("https://api.test.local") }, exchanger);
        return (new DocumentTools(new HttpContextAccessor { HttpContext = context }, api, NullLogger<DocumentTools>.Instance), exchanger);
    }

    [Fact]
    public async Task A_tool_refuses_a_caller_without_its_scope_even_with_no_middleware()
    {
        var (tools, exchanger) = ToolsFor("openid docvault:read");

        await Assert.ThrowsAsync<McpException>(() => tools.DeleteDocument(Guid.NewGuid().ToString(), CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => tools.CreateDocument("title", CancellationToken.None));

        // Refused before the exchange: nothing left this process.
        Assert.Equal(0, exchanger.Calls);
    }

    [Fact]
    public async Task A_tool_refuses_a_caller_with_no_scopes()
    {
        var (tools, exchanger) = ToolsFor(string.Empty);

        await Assert.ThrowsAsync<McpException>(() => tools.ListDocuments(CancellationToken.None));
        Assert.Equal(0, exchanger.Calls);
    }
}
