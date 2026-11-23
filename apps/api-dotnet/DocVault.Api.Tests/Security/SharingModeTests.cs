// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Net.Http.Json;
using DocVault.Api.Domain;
using DocVault.Api.Sharing;
using DocVault.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocVault.Api.Tests.Security;

/// <summary>
/// Sharing:Mode, and the settings beside it: there is no safe default, and no setting that
/// turns a private document into one the whole tenant can read.
/// </summary>
public sealed class SharingModeTests(DocVaultApiFactory tenantOnly) : IClassFixture<DocVaultApiFactory>
{
    [Fact]
    public async Task Without_OpenFGA_a_private_document_is_refused_not_shown_to_the_tenant()
    {
        var token = tenantOnly.Tokens.CreateToken(apiRoles: ["doc.editor"], groups: ["/acme"]);

        var response = await tenantOnly.CreateClientWithToken(token)
            .PostAsJsonAsync("/documents", new { title = "Secret", visibility = "private" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(
            tenantOnly.Services.GetRequiredService<IDocumentStore>().ListForTenant("acme", includeClassified: true),
            d => d.Title == "Secret");
    }

    [Fact]
    public async Task Without_OpenFGA_there_is_nothing_to_share()
    {
        var token = tenantOnly.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/acme"]);
        var client = tenantOnly.CreateClientWithToken(token);
        var roadmap = InMemoryDocumentStore.Seeded.AcmeRoadmap;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/documents/{roadmap}/shares")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/documents/{roadmap}/shares", new { username = "carol" })).StatusCode);
    }

    [Fact]
    public async Task Without_OpenFGA_only_an_administrator_deletes_as_before()
    {
        var editor = tenantOnly.Tokens.CreateToken(apiRoles: ["doc.editor"], groups: ["/acme"]);
        var created = await tenantOnly.CreateClientWithToken(editor).PostAsJsonAsync("/documents", new { title = "Mine" });
        var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["id"]!.ToString();

        // Owning it is not enough here: there is no OpenFGA to say who owns what.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantOnly.CreateClientWithToken(editor).DeleteAsync($"/documents/{id}")).StatusCode);

        var admin = tenantOnly.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/acme"]);
        Assert.Equal(HttpStatusCode.NoContent, (await tenantOnly.CreateClientWithToken(admin).DeleteAsync($"/documents/{id}")).StatusCode);
    }

    [Fact]
    public void The_API_refuses_to_start_without_a_mode()
    {
        using var factory = new FactoryWith(new() { ["Sharing:Mode"] = null });

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Sharing:Mode must be set", error.Message);
    }

    [Fact]
    public void The_API_refuses_to_start_on_a_model_the_store_does_not_have()
    {
        using var factory = new OpenFgaFactoryWith(fga => fga.KnownModelId = "01JSOMEOTHERMODEL000000000");

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains($"has no authorization model {FakeOpenFga.ModelId}", error.Message);
    }

    [Fact]
    public void The_API_refuses_to_start_when_OpenFGA_is_unreachable()
    {
        using var factory = new OpenFgaFactoryWith(fga => fga.Down = true);

        Assert.Throws<DependencyUnavailableException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("OpenFgaUrl", "localhost:8090", "must be an absolute http(s) URL")]
    [InlineData("OpenFgaUrl", "http://openfga.example.com", "plain HTTP to a host that is not loopback")]
    [InlineData("ModelId", "", "Sharing:ModelId must be set")]
    [InlineData("StoreId", "", "Sharing:StoreId must be set")]
    [InlineData("OpenFgaApiKey", "", "Sharing:OpenFgaApiKey must be set")]
    [InlineData("DirectoryClientSecret", "", "Sharing:DirectoryClientSecret must be set")]
    public void Each_unsafe_or_missing_OpenFGA_setting_is_named(string property, string value, string expected)
    {
        var settings = new SharingSettings
        {
            Mode = SharingMode.OpenFga,
            OpenFgaUrl = "https://openfga.example.com",
            OpenFgaApiKey = "k",
            StoreId = "s",
            ModelId = "m",
            DirectoryClientSecret = "d",
        };
        typeof(SharingSettings).GetProperty(property)!.SetValue(settings, value);

        Assert.Contains(settings.Problems(), p => p.Contains(expected));
    }

    [Fact]
    public void TenantOnly_needs_no_OpenFGA_settings()
    {
        Assert.Empty(new SharingSettings { Mode = SharingMode.TenantOnly }.Problems());
    }

    private sealed class FactoryWith(Dictionary<string, string?> settings) : DocVaultApiFactory
    {
        protected override Dictionary<string, string?> SharingSettings() => settings;
    }

    private sealed class OpenFgaFactoryWith(Action<FakeOpenFga> arrange) : OpenFgaApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            arrange(Fga);
            base.ConfigureWebHost(builder);
        }
    }
}
