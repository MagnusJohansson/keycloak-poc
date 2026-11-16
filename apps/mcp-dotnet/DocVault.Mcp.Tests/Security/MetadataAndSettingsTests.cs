// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Text.Json;
using DocVault.Mcp.Tests.Support;
using Microsoft.Extensions.Configuration;

namespace DocVault.Mcp.Tests.Security;

public sealed class MetadataTests(McpServerFactory factory) : IClassFixture<McpServerFactory>
{
    [Fact]
    public async Task Publishes_protected_resource_metadata_without_a_token()
    {
        var response = await factory.CreateClient().GetAsync("/.well-known/oauth-protected-resource/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(TestTokenIssuer.Resource, metadata.GetProperty("resource").GetString());
        Assert.Equal([TestTokenIssuer.Issuer], metadata.GetProperty("authorization_servers").EnumerateArray().Select(a => a.GetString()));
        // The minimum a client should ask for first; write and delete come by challenge.
        Assert.Equal(["docvault:read"], metadata.GetProperty("scopes_supported").EnumerateArray().Select(a => a.GetString()));
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/other")]
    [InlineData("/.well-known/oauth-protected-resource/mcp/extra")]
    public async Task Describes_no_resource_but_its_own(string path)
    {
        // By default the SDK derives `resource` from the request and answers under any
        // suffix. This server serves one fixed document.
        var response = await factory.CreateClient().GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_resource_does_not_follow_the_Host_header()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp");
        request.Headers.Host = "evil.example";

        var response = await factory.CreateClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("evil.example", body);
    }
}

/// <summary>A wrong setting stops the start. None is silently defaulted.</summary>
public sealed class SettingsTests
{
    private static McpSettings Load(Dictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["Keycloak:Authority"] = "https://id.example/realms/docvault",
            ["Mcp:ResourceUrl"] = "https://mcp.example/mcp",
            ["Mcp:ClientSecret"] = "s3cret",
            ["DocVault:ApiBaseUrl"] = "https://api.example",
            ["DocVault:ApiAudience"] = "docvault-api",
        };
        foreach (var (k, v) in overrides) values[k] = v;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return McpSettings.Load(configuration);
    }

    [Fact]
    public void Loads_a_complete_configuration()
    {
        var settings = Load([]);

        Assert.Equal("/mcp", settings.McpPath);
        Assert.Equal("/.well-known/oauth-protected-resource/mcp", settings.ResourceMetadataPath);
        Assert.Equal("https://mcp.example/.well-known/oauth-protected-resource/mcp", settings.ResourceMetadataUrl);
        Assert.Equal("https://id.example/realms/docvault/protocol/openid-connect/token", settings.TokenEndpoint);
        Assert.True(settings.RequireHttpsMetadata);
    }

    [Theory]
    [InlineData("Mcp:ClientSecret", "")] // no secret: no exchange, so no start
    [InlineData("Mcp:ClientSecret", null)]
    [InlineData("Mcp:ResourceUrl", "mcp.example/mcp")] // no scheme
    [InlineData("Mcp:ResourceUrl", "localhost:5002/mcp")] // parses as scheme "localhost"
    [InlineData("Mcp:ResourceUrl", "https://mcp.example/mcp/")] // would never equal `aud`
    [InlineData("Mcp:ResourceUrl", "https://mcp.example/mcp#frag")]
    [InlineData("Mcp:ResourceUrl", "https://mcp.example/mcp?x=1")]
    [InlineData("Mcp:ResourceUrl", "http://mcp.example/mcp")] // clients' tokens in clear text
    [InlineData("DocVault:ApiBaseUrl", "http://api.example")] // user tokens in clear text
    [InlineData("DocVault:ApiBaseUrl", "https://api.example/v1")] // the path would be dropped silently
    [InlineData("DocVault:ApiAudience", "")]
    [InlineData("Keycloak:Authority", "")]
    public void Refuses_to_start_with_a_missing_or_unsafe_setting(string key, string? value)
    {
        Assert.Throws<InvalidOperationException>(() => Load(new() { [key] = value }));
    }

    [Fact]
    public void Requires_HTTPS_metadata_unless_told_otherwise()
    {
        // Not derived from the environment name: unset means safe.
        Assert.True(Load([]).RequireHttpsMetadata);
    }

    [Fact]
    public void Refuses_plain_HTTP_metadata_from_a_remote_authority()
    {
        Assert.Throws<InvalidOperationException>(() => Load(new()
        {
            ["Keycloak:Authority"] = "http://id.example/realms/docvault",
            ["Keycloak:RequireHttpsMetadata"] = "false",
        }));
    }

    [Fact]
    public void Allows_plain_HTTP_on_loopback_for_the_local_lab()
    {
        var settings = Load(new()
        {
            ["Keycloak:Authority"] = "http://localhost:8080/realms/docvault",
            ["Keycloak:RequireHttpsMetadata"] = "false",
            ["Mcp:ResourceUrl"] = "http://localhost:5002/mcp",
            ["DocVault:ApiBaseUrl"] = "http://localhost:5001",
        });

        Assert.False(settings.RequireHttpsMetadata);
    }
}
