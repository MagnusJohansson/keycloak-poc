// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using DocVault.Api.Auth;
using DocVault.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocVault.Api.Tests.Security;

/// <summary>
/// The access log is how the API tells a request an assistant made for a user from
/// one the user made themselves: the user is the same, the client is not.
/// </summary>
public sealed class AccessLogTests(DocVaultApiFactory factory) : IClassFixture<DocVaultApiFactory>
{
    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == AccessLogMiddleware.Category)
                {
                    lines.Enqueue(formatter(state, exception));
                }
            }
        }
    }

    private (HttpClient Client, Capture Log) ClientFor(string? token)
    {
        var log = new Capture();
        var client = factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(log)))
            .CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return (client, log);
    }

    [Fact]
    public async Task A_request_is_logged_with_the_user_and_the_client_it_came_through()
    {
        // What the API receives after the MCP server's token exchange.
        var token = factory.Tokens.CreateToken(
            username: "alice", apiRoles: ["doc.reader"], groups: ["/acme"], client: "http://localhost:5002/mcp");
        var (client, log) = ClientFor(token);

        var response = await client.GetAsync("/documents");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(log.Lines);
        Assert.Contains("method=GET", line);
        Assert.Contains("route=/documents/", line);
        Assert.Contains("status=200", line);
        Assert.Contains("user=alice", line);
        Assert.Contains("client=http://localhost:5002/mcp", line);
        Assert.Matches("trace=[0-9a-f]{32}", line);
    }

    [Fact]
    public async Task A_request_authorization_refuses_is_logged_too()
    {
        // The assistant had the scope; the user does not have the role. Creating needs
        // doc.editor, and the policy refuses before the endpoint runs.
        var token = factory.Tokens.CreateToken(
            username: "alice", apiRoles: ["doc.reader"], groups: ["/acme"], client: "http://localhost:5002/mcp");
        var (client, log) = ClientFor(token);

        var response = await client.PostAsJsonAsync("/documents", new { title = "refused" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var line = Assert.Single(log.Lines);
        Assert.Contains("method=POST", line);
        Assert.Contains("status=403", line);
        Assert.Contains("client=http://localhost:5002/mcp", line);
    }

    [Fact]
    public async Task The_log_holds_the_routes_pattern_not_the_callers_path()
    {
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/acme"], client: "docvault-web-react");
        var (client, log) = ClientFor(token);
        var id = Guid.NewGuid();

        await client.DeleteAsync($"/documents/{id}");
        // %0A is a line break once decoded: a path is not safe to write to a log.
        await client.GetAsync("/nowhere/%0Aaccess%20method=GET%20user=carol");

        Assert.Equal(2, log.Lines.Count);
        Assert.All(log.Lines, line =>
        {
            Assert.DoesNotContain("\n", line);
            Assert.DoesNotContain(id.ToString(), line);
            Assert.DoesNotContain("carol", line);
        });
        Assert.Contains("route=(no route)", log.Lines.Last());
    }

    [Fact]
    public async Task The_log_holds_a_known_method_name_not_the_callers_text()
    {
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.reader"], groups: ["/acme"], client: "docvault-web-react");
        var (client, log) = ClientFor(token);

        await client.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), "/documents"));

        var line = Assert.Single(log.Lines);
        Assert.Contains("method=OTHER", line);
        Assert.DoesNotContain("PROPFIND", line);
    }

    [Fact]
    public async Task A_request_without_a_valid_token_is_not_in_the_access_log()
    {
        var (anonymous, anonymousLog) = ClientFor(null);
        var (forged, forgedLog) = ClientFor(factory.Tokens.CreateToken(audience: "something-else", client: "docvault-web-react"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/documents")).StatusCode);

        // Nothing a refused token says is known to be true.
        Assert.Empty(anonymousLog.Lines);
        Assert.Empty(forgedLog.Lines);
    }
}
