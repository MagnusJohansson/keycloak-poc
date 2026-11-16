// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using DocVault.Mcp;
using DocVault.Mcp.Auth;
using DocVault.Mcp.Downstream;
using DocVault.Mcp.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

var settings = McpSettings.Load(builder.Configuration);
builder.Services.AddSingleton(settings);

builder.Services
    .AddAuthentication(options =>
    {
        // JwtBearer validates the token; the MCP scheme writes the 401 challenge
        // that tells a client where this server's metadata is.
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.Authority = settings.Authority;
        options.RequireHttpsMetadata = settings.RequireHttpsMetadata;

        // Keep Keycloak's claim names ("scope", "azp", "preferred_username").
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Authority,

            // The check MCP requires: the token was issued for THIS server. A token
            // for the DocVault API, or for another MCP server in the same realm, has
            // a valid signature and issuer and must still be refused.
            ValidateAudience = true,
            ValidAudience = settings.Resource,
            // .NET treats "…/mcp/" and "…/mcp" as the same audience unless told not
            // to. They are different resources; the comparison here is exact.
            IgnoreTrailingSlashWhenValidatingAudience = false,
            // And it is the ONLY audience. .NET accepts a token if any of several
            // audiences matches; a token that is also for something else was not
            // issued specifically for this server.
            AudienceValidator = (audiences, _, parameters) =>
                audiences.Count() == 1 && audiences.Single() == parameters.ValidAudience,

            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),

            // Never "none", never a symmetric algorithm with a public key as secret.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256],

            NameClaimType = "preferred_username",
        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("DocVault.Mcp.Auth")
                    .LogWarning("Token refused: {Reason}", context.Exception.Message);
                return Task.CompletedTask;
            },
        };
    })
    .AddMcp(options =>
    {
        // Fixed and absolute, not derived from the request. By default the SDK builds
        // `resource` from the Host header and serves metadata under any path suffix,
        // and with a relative URI it still builds the 401's resource_metadata link
        // from the Host header. Absolute, the link is constant and the document is
        // served only for this server's own host.
        options.ResourceMetadataUri = new Uri(settings.ResourceMetadataUrl, UriKind.Absolute);
        options.ResourceMetadata = new()
        {
            Resource = settings.Resource,
            AuthorizationServers = { settings.Authority },
            ScopesSupported = [.. ToolScopes.Initial],
            ResourceName = "DocVault documents",
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

builder.Services.AddHttpClient(KeycloakTokenExchanger.HttpClientName);
builder.Services.AddHttpClient(DocVaultApiClient.HttpClientName, client => client.BaseAddress = settings.ApiBaseUrl);
builder.Services.AddScoped<ITokenExchanger>(sp => new KeycloakTokenExchanger(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(KeycloakTokenExchanger.HttpClientName),
    settings,
    sp.GetRequiredService<ILogger<KeycloakTokenExchanger>>()));
builder.Services.AddScoped(sp => new DocVaultApiClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(DocVaultApiClient.HttpClientName),
    sp.GetRequiredService<ITokenExchanger>()));

builder.Services
    .AddMcpServer()
    .WithTools<DocumentTools>()
    // Stateless: every request carries its own token and nothing is kept between
    // requests, so there is no session to hijack or to pin to one instance.
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ScopeChallengeMiddleware>();

app.MapMcp(settings.McpPath).RequireAuthorization();

app.Run();

public partial class Program;
