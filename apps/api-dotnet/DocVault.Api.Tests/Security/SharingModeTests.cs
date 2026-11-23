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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        var startup = new StartupFailures();
        using var factory = new FactoryWith(new() { ["Sharing:Mode"] = null }, startup);

        var error = startup.Of<OptionsValidationException>(factory);
        Assert.Contains("Sharing:Mode must be set", error.Message);
    }

    [Fact]
    public void The_API_refuses_to_start_on_a_model_the_store_does_not_have()
    {
        var startup = new StartupFailures();
        using var factory = new OpenFgaFactoryWith(fga => fga.KnownModelId = "01JSOMEOTHERMODEL000000000", startup);

        var error = startup.Of<InvalidOperationException>(factory);
        Assert.Contains($"has no authorization model {FakeOpenFga.ModelId}", error.Message);
    }

    [Fact]
    public void The_API_refuses_to_start_when_OpenFGA_is_unreachable()
    {
        var startup = new StartupFailures();
        using var factory = new OpenFgaFactoryWith(fga => fga.Down = true, startup);

        startup.Of<DependencyUnavailableException>(factory);
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

    private sealed class FactoryWith(Dictionary<string, string?> settings, StartupFailures startup) : DocVaultApiFactory
    {
        protected override Dictionary<string, string?> SharingSettings() => settings;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(startup));
        }
    }

    private sealed class OpenFgaFactoryWith(Action<FakeOpenFga> arrange, StartupFailures startup) : OpenFgaApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            arrange(Fga);
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(startup));
        }
    }

    /// <summary>
    /// Why the host did not start. WebApplicationFactory usually rethrows the API's own
    /// exception, but not always: when the failed host is disposed before the factory asks
    /// it for IHostApplicationLifetime, the factory throws ObjectDisposedException instead
    /// (seen once in CI). The host logs the real exception either way, so that is read too.
    /// </summary>
    private sealed class StartupFailures : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Exception> _logged = new();

        public T Of<T>(WebApplicationFactory<Program> factory)
            where T : Exception
        {
            var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            if (thrown is T expected)
            {
                return expected;
            }

            Assert.IsType<ObjectDisposedException>(thrown);
            return Assert.Single(_logged.OfType<T>().Distinct());
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null && IsEnabled(logLevel))
            {
                _logged.Enqueue(exception);
            }
        }

        public void Dispose()
        {
        }
    }
}
