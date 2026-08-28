// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using DocVault.Api.Tests.Support;

namespace DocVault.Api.Tests.Security;

public sealed class StepUpTests(DocVaultApiFactory factory) : IClassFixture<DocVaultApiFactory>
{
    [Fact]
    public async Task Classified_access_is_refused_without_a_stepped_up_acr()
    {
        // Has the right role, but only authenticated with a password.
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/acme/legal"], acr: "bronze");

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents/classified");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Refusal_tells_the_client_how_to_recover()
    {
        // Per RFC 9470. Without this header the SPA cannot know that re-authenticating
        // would help, and the user hits a dead end.
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/acme/legal"], acr: "bronze");

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents/classified");
        var challenge = response.Headers.WwwAuthenticate.ToString();

        Assert.Contains("insufficient_user_authentication", challenge);
        Assert.Contains("acr_values=\"silver\"", challenge);
    }

    [Fact]
    public async Task Classified_access_is_granted_after_stepping_up()
    {
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/acme/legal"], acr: "silver");

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents/classified");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Stepping_up_does_not_substitute_for_the_role()
    {
        // MFA proves presence, not permission. A reader with silver ACR still may not
        // read classified material.
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.reader"], groups: ["/acme"], acr: "silver");

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents/classified");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
