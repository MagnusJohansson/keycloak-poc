// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DocVault.Api.Tests.Support;

/// <summary>
/// Runs the real API pipeline — real JwtBearer handler, real claims transformation, real
/// policies — but validates signatures against <see cref="TestTokenIssuer"/> instead of
/// fetching JWKS from a live Keycloak.
/// </summary>
/// <remarks>
/// Only the signing key source is swapped. Everything the tests actually assert on
/// (audience, issuer, lifetime, algorithm, role mapping, policy evaluation) is the
/// production code path, so a passing test means the production configuration works.
/// </remarks>
public sealed class DocVaultApiFactory : WebApplicationFactory<Program>
{
    public TestTokenIssuer Tokens { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Keycloak:Authority"] = TestTokenIssuer.DefaultIssuer,
                ["Keycloak:Audience"] = TestTokenIssuer.DefaultAudience,
                ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // No network: hand the handler our public key directly instead of letting
                // it discover one. Keeps the unit suite fast and offline.
                options.Authority = null!;
                options.MetadataAddress = null!;
                options.RequireHttpsMetadata = false;
                options.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration();
                options.TokenValidationParameters.IssuerSigningKey = Tokens.SigningKey;
                options.TokenValidationParameters.IssuerSigningKeys = [Tokens.SigningKey];

                // Stated explicitly rather than inherited from configuration. Under minimal
                // hosting, the app's own appsettings.json is applied after the test host's
                // in-memory source, so relying on configuration override here silently
                // leaves the production issuer in place and every test 401s.
                options.TokenValidationParameters.ValidIssuer = TestTokenIssuer.DefaultIssuer;
                options.TokenValidationParameters.ValidAudience = TestTokenIssuer.DefaultAudience;
            });
        });
    }

    public HttpClient CreateClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Tokens.Dispose();
        }

        base.Dispose(disposing);
    }
}
