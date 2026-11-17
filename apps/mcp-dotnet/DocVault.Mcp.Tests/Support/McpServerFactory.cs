// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocVault.Mcp.Downstream;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace DocVault.Mcp.Tests.Support;

/// <summary>
/// Boots the real server in memory, with its real authentication and middleware.
/// Only two things are replaced: the signing key it trusts (the test issuer's, so no
/// Keycloak is needed) and the network (the downstream stub).
/// </summary>
public sealed class McpServerFactory : WebApplicationFactory<Program>
{
    public const string ClientSecret = "test-client-secret";

    public TestTokenIssuer Tokens { get; } = new();
    public DownstreamStub Downstream { get; } = new();

    /// <summary>The audit trail the server writes: the real file sink, in a temp directory.</summary>
    public string AuditFile { get; } = Path.Combine(Path.GetTempPath(), $"docvault-mcp-audit-{Guid.NewGuid():N}.jsonl");

    public McpServerFactory()
    {
        // Requests arrive at the server's own URL, as they do in the lab. The
        // metadata document is served only for the configured host.
        ClientOptions.BaseAddress = new Uri("https://mcp.test.local");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // UseSetting, not ConfigureAppConfiguration: Program reads its settings while
        // building, before configuration added by a later callback would be visible.
        builder.UseSetting("Keycloak:Authority", TestTokenIssuer.Issuer);
        // appsettings.json allows plain-HTTP metadata for the loopback lab; with this
        // remote test authority the server would (rightly) refuse to start.
        builder.UseSetting("Keycloak:RequireHttpsMetadata", "true");
        builder.UseSetting("Mcp:ResourceUrl", TestTokenIssuer.Resource);
        builder.UseSetting("Mcp:ClientSecret", ClientSecret);
        builder.UseSetting("DocVault:ApiBaseUrl", "https://api.test.local");
        builder.UseSetting("DocVault:ApiAudience", TestTokenIssuer.ApiAudience);
        builder.UseSetting("Audit:File", AuditFile);

        builder.ConfigureTestServices(services =>
        {
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Static signing key instead of a metadata fetch. Everything else about
                // validation (issuer, audience, lifetime, algorithms) is the real code's.
                options.Authority = null!;
                options.MetadataAddress = null!;
                options.RequireHttpsMetadata = false;
                options.Configuration = new OpenIdConnectConfiguration();
                options.TokenValidationParameters.IssuerSigningKey = Tokens.SigningKey;
                options.TokenValidationParameters.IssuerSigningKeys = [Tokens.SigningKey];
            });

            foreach (var name in new[] { KeycloakTokenExchanger.HttpClientName, DocVaultApiClient.HttpClientName })
            {
                services.Configure<HttpClientFactoryOptions>(name, options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = Downstream));
            }
        });
    }

    /// <summary>POST one JSON-RPC message to /mcp, as an MCP client does.</summary>
    public Task<HttpResponseMessage> PostAsync(string? token, object message, string path = "/mcp") =>
        PostRawAsync(token, new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json"), path);

    /// <summary>POST arbitrary content, for bodies a well-behaved client would not send.</summary>
    public async Task<HttpResponseMessage> PostRawAsync(string? token, HttpContent content, string path = "/mcp")
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    public Task<HttpResponseMessage> ListToolsAsync(string? token) =>
        PostAsync(token, new { jsonrpc = "2.0", id = 1, method = "tools/list" });

    public Task<HttpResponseMessage> CallToolAsync(string? token, string name, object? arguments = null) =>
        PostAsync(token, new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = arguments ?? new { } } });

    /// <summary>The JSON-RPC message in a response, whether sent as JSON or as one SSE event.</summary>
    public static async Task<JsonElement> ReadMessageAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var data = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
        return JsonDocument.Parse(data is null ? body : data["data:".Length..]).RootElement.Clone();
    }

    /// <summary>The audit file as written: one line per record.</summary>
    public string[] AuditLines()
    {
        if (!File.Exists(AuditFile))
        {
            return [];
        }

        // The server keeps the file open for writing.
        using var stream = new FileStream(AuditFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The audit records written after the first <paramref name="skip"/>.</summary>
    public JsonElement[] AuditRecords(int skip = 0) =>
        [.. AuditLines().Skip(skip).Select(line => JsonDocument.Parse(line).RootElement.Clone())];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Tokens.Dispose();
            Downstream.Dispose();
        }

        base.Dispose(disposing);

        if (disposing)
        {
            File.Delete(AuditFile);
        }
    }
}
