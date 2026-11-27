// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using DocVault.Api.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocVault.Api.Tests.Support;

/// <summary>The API with Sharing:Mode OpenFga, over <see cref="FakeOpenFga"/> and <see cref="FakeDirectory"/>.</summary>
public class OpenFgaApiFactory : DocVaultApiFactory
{
    public FakeOpenFga Fga { get; } = new();

    public FakeDirectory Directory { get; } = new();

    protected override Dictionary<string, string?> SharingSettings() => new()
    {
        ["Sharing:Mode"] = "OpenFga",
        ["Sharing:OpenFgaUrl"] = "http://localhost:8090",
        ["Sharing:OpenFgaApiKey"] = "test-fga-key",
        ["Sharing:StoreId"] = FakeOpenFga.StoreId,
        ["Sharing:ModelId"] = FakeOpenFga.ModelId,
        ["Sharing:DirectoryClientSecret"] = "unused-by-the-fake",
        // Off here; SharingTimingTests sets one and measures it.
        ["Sharing:PersonLookupFloor"] = LookupFloor,
    };

    protected virtual string LookupFloor => "00:00:00";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<OpenFgaClient>().ConfigurePrimaryHttpMessageHandler(() => Fga);
            services.RemoveAll<IPeopleDirectory>();
            services.AddSingleton<IPeopleDirectory>(Directory);
        });
    }
}
