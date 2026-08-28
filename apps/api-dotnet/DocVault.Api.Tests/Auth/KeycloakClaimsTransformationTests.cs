// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using DocVault.Api.Auth;

namespace DocVault.Api.Tests.Auth;

public sealed class KeycloakClaimsTransformationTests
{
    private static KeycloakClaimsTransformation CreateSubject() =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Keycloak:Audience"] = "docvault-api" })
                .Build(),
            NullLogger<KeycloakClaimsTransformation>.Instance);

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test", nameType: "preferred_username", roleType: ClaimTypes.Role));

    [Fact]
    public async Task Maps_realm_roles_into_role_claims()
    {
        var principal = PrincipalWith(
            new Claim("realm_access", """{"roles":["platform-admin"]}""", JsonClaimValueTypes.Json));

        var result = await CreateSubject().TransformAsync(principal);

        Assert.True(result.IsInRole("platform-admin"));
    }

    [Fact]
    public async Task Maps_client_roles_for_this_api_only()
    {
        var principal = PrincipalWith(new Claim(
            "resource_access",
            """{"docvault-api":{"roles":["doc.editor"]},"some-other-app":{"roles":["doc.admin"]}}""",
            JsonClaimValueTypes.Json));

        var result = await CreateSubject().TransformAsync(principal);

        Assert.True(result.IsInRole("doc.editor"));

        // A role granted on a DIFFERENT client must not leak into this API's authorization.
        // Otherwise any client in the realm could mint itself admin rights here.
        Assert.False(result.IsInRole("doc.admin"));
    }

    [Fact]
    public async Task Drops_the_default_roles_composite()
    {
        var principal = PrincipalWith(
            new Claim("realm_access", """{"roles":["default-roles-docvault","platform-admin"]}""", JsonClaimValueTypes.Json));

        var result = await CreateSubject().TransformAsync(principal);

        Assert.False(result.IsInRole("default-roles-docvault"));
        Assert.True(result.IsInRole("platform-admin"));
    }

    [Fact]
    public async Task Is_idempotent_across_repeated_invocations()
    {
        // IClaimsTransformation runs per request; without the guard, roles accumulate.
        var principal = PrincipalWith(
            new Claim("resource_access", """{"docvault-api":{"roles":["doc.editor"]}}""", JsonClaimValueTypes.Json));

        var subject = CreateSubject();
        await subject.TransformAsync(principal);
        await subject.TransformAsync(principal);
        await subject.TransformAsync(principal);

        Assert.Single(principal.FindAll(ClaimTypes.Role), c => c.Value == "doc.editor");
    }

    [Fact]
    public async Task Grants_nothing_when_the_claim_is_malformed()
    {
        // A broken token must never fail open.
        var principal = PrincipalWith(new Claim("resource_access", "this-is-not-json"));

        var result = await CreateSubject().TransformAsync(principal);

        Assert.Empty(result.FindAll(ClaimTypes.Role));
    }
}
