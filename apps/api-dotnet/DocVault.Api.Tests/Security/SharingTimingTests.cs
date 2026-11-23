// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DocVault.Api.Sharing;
using DocVault.Api.Tests.Support;

namespace DocVault.Api.Tests.Security;

/// <summary>
/// A share to someone in another tenant and a share to nobody answer the same 404. They
/// must also take the same time: against the lab's LDAP directory, a person who exists
/// took ~300 ms to look up and nobody ~90 ms (found by review).
/// </summary>
[Collection(Timing.Name)]
public sealed class SharingTimingTests(SharingTimingTests.Factory factory) : IClassFixture<SharingTimingTests.Factory>
{
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(400);

    public sealed class Factory : OpenFgaApiFactory
    {
        protected override string LookupFloor => Floor.ToString();
    }

    [Fact]
    public async Task Someone_elsewhere_and_nobody_take_the_same_time()
    {
        var owner = Guid.NewGuid().ToString("D");
        var client = factory.CreateClientWithToken(factory.Tokens.CreateToken(apiRoles: ["doc.editor"], groups: ["/acme"], sub: owner));
        var created = await client.PostAsJsonAsync("/documents", new { title = "Timed", visibility = "private" });
        var id = (string)(await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
        factory.Fga.Allow($"user:{owner}", "can_view", $"document:{id}");
        factory.Fga.Allow($"user:{owner}", "can_share", $"document:{id}");

        // As the directory behaves: a person who exists takes longer to look up.
        factory.Directory.AddPerson("frank", Guid.NewGuid(), "globex");
        factory.Directory.ExistingPersonDelay = TimeSpan.FromMilliseconds(150);

        async Task<TimeSpan> Timed(string username)
        {
            var clock = Stopwatch.StartNew();
            var response = await client.PostAsJsonAsync($"/documents/{id}/shares", new { username });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            return clock.Elapsed;
        }

        await Timed("warm-up");
        var elsewhere = await Timed("frank");
        var nobody = await Timed("no-such-person");

        Assert.True(elsewhere >= Floor && nobody >= Floor, $"{elsewhere.TotalMilliseconds} / {nobody.TotalMilliseconds} ms");
        Assert.True((elsewhere - nobody).Duration() < TimeSpan.FromMilliseconds(100),
            $"{elsewhere.TotalMilliseconds} / {nobody.TotalMilliseconds} ms");
    }

    [Theory]
    [InlineData("legal", true)]
    [InlineData("a.b-c_d", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("-x", false)]
    [InlineData("legal\n", false)]
    [InlineData("le#gal", false)]
    [InlineData("", false)]
    public void A_name_that_becomes_part_of_an_id_or_a_path(string name, bool safe) =>
        Assert.Equal(safe, FgaNames.IsSafeName(name));
}

/// <summary>Runs alone: other test classes running beside it make the clock say little.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Timing
{
    public const string Name = "timing";
}
