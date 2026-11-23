// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using DocVault.Api.Domain;
using Microsoft.Extensions.Options;

namespace DocVault.Api.Sharing;

public static class SharingRegistration
{
    public static IServiceCollection AddDocVaultSharing(this IServiceCollection services, IConfiguration configuration)
    {
        // Validated when the host starts, so a missing or unsafe setting stops the API
        // before it answers anything, with every problem listed at once.
        services.AddOptions<SharingSettings>()
            .Bind(configuration.GetSection(SharingSettings.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SharingSettings>, SharingSettingsValidator>();

        services.AddHttpClient<OpenFgaClient>((sp, client) =>
            OpenFgaClient.Configure(client, sp.GetRequiredService<IOptions<SharingSettings>>().Value));
        services.AddHttpClient<IPeopleDirectory, KeycloakPeopleDirectory>(client => client.Timeout = KeycloakPeopleDirectory.Timeout);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DirectoryToken>();

        services.AddSingleton<TenantOnlyAuthorizer>();
        services.AddTransient<OpenFgaDocumentAuthorizer>();
        services.AddTransient<IDocumentAuthorizer>(sp =>
            sp.GetRequiredService<IOptions<SharingSettings>>().Value.Mode == SharingMode.OpenFga
                ? sp.GetRequiredService<OpenFgaDocumentAuthorizer>()
                : sp.GetRequiredService<TenantOnlyAuthorizer>());

        services.AddHostedService<SharingStartup>();
        services.AddExceptionHandler<DependencyUnavailableExceptionHandler>();
        return services;
    }
}

/// <summary>Turns <see cref="SharingSettings.Problems"/> into a startup failure that names them.</summary>
internal sealed class SharingSettingsValidator : IValidateOptions<SharingSettings>
{
    public ValidateOptionsResult Validate(string? name, SharingSettings options)
    {
        var problems = options.Problems().ToList();
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}

/// <summary>
/// At startup, with OpenFGA: refuse to run against a model the store does not have, then
/// write the seeded documents' tuples.
/// </summary>
/// <remarks>
/// The seeded documents have fixed ids and their tuples are written with on_duplicate:
/// ignore, so every start writes the same tuples. Documents created while the API ran are
/// lost when it stops (the store is in memory); their tuples stay in OpenFGA and grant
/// nothing, since no document has those ids.
/// </remarks>
internal sealed class SharingStartup(
    IOptions<SharingSettings> options,
    IServiceProvider services,
    IDocumentStore store,
    ILogger<SharingStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.Mode != SharingMode.OpenFga)
        {
            logger.LogInformation("Sharing:Mode is {Mode}: every document is visible to its whole tenant; private documents and sharing are off.", settings.Mode);
            return;
        }

        using var scope = services.CreateScope();
        var fga = scope.ServiceProvider.GetRequiredService<OpenFgaClient>();

        if (!await fga.ModelExistsAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"OpenFGA store {settings.StoreId} has no authorization model {settings.ModelId}. " +
                "Run `make seed` and start the API with `make api`, which reads both from 25-openfga's outputs.");
        }

        var authorizer = scope.ServiceProvider.GetRequiredService<OpenFgaDocumentAuthorizer>();
        foreach (var id in new[]
        {
            InMemoryDocumentStore.Seeded.AcmeRoadmap, InMemoryDocumentStore.Seeded.AcmeHandbook,
            InMemoryDocumentStore.Seeded.AcmeMemo, InMemoryDocumentStore.Seeded.GlobexSuppliers,
        })
        {
            if (store.Get(id) is { } document)
            {
                await authorizer.CreatedAsync(document, cancellationToken);
            }
        }

        logger.LogInformation("Sharing:Mode is OpenFga: store {Store}, model {Model}.", settings.StoreId, settings.ModelId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
