// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using Microsoft.IdentityModel.Tokens;
using DocVault.Api.Tests.Support;
using System.Security.Cryptography;

namespace DocVault.Api.Tests.Security;

/// <summary>
/// Adversarial tests. Each one forges a token a real Keycloak would never issue and
/// asserts the API rejects it. These are the checks that turn "authentication is
/// configured" into "authentication actually works".
/// </summary>
public sealed class TokenValidationTests(DocVaultApiFactory factory) : IClassFixture<DocVaultApiFactory>
{
    [Fact]
    public async Task Accepts_a_well_formed_token()
    {
        // Control case. If this fails, every rejection below is meaningless.
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.reader"], groups: ["/acme/engineering"]);

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_an_unsigned_alg_none_token()
    {
        // The canonical JWT attack: strip the signature and set alg to "none". A naive
        // decoder that trusts the header would grant this forged doc.admin token.
        var response = await factory
            .CreateClientWithToken(TestTokenIssuer.CreateUnsignedToken())
            .GetAsync("/documents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_token_signed_by_an_unknown_key()
    {
        // An attacker who mints their own keypair must not be able to impersonate the IdP.
        using var rogue = RSA.Create(2048);
        var rogueKey = new RsaSecurityKey(rogue) { KeyId = "rogue" };
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], overrideKey: rogueKey);

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_token_minted_for_a_different_audience()
    {
        // A token legitimately issued for another API in the same realm must not be
        // replayable here. This is exactly what the audience mapper exists to enable.
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], audience: "some-other-api");

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_token_from_a_different_realm()
    {
        // Multi-realm deployments make this a live risk: same server, same signing
        // infrastructure, different issuer.
        var token = factory.Tokens.CreateToken(
            apiRoles: ["doc.admin"],
            issuer: "https://test-issuer.local/realms/some-other-realm");

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_an_expired_token()
    {
        // Well outside the 30s ClockSkew allowance.
        var token = factory.Tokens.CreateToken(
            apiRoles: ["doc.reader"],
            expires: DateTime.UtcNow.AddMinutes(-10));

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_request_with_no_token_at_all()
    {
        var response = await factory.CreateClient().GetAsync("/documents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Allows_anonymous_access_to_health()
    {
        // Proves the deny-by-default posture is opt-out, not accidental.
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
