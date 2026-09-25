// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Net.Http.Json;
using DocVault.Api.Tests.Support;

namespace DocVault.Api.Tests.Security;

/// <summary>
/// Authorization, as distinct from authentication: the caller is who they say they are,
/// but may not do what they are asking to do.
/// </summary>
public sealed class AuthorizationTests(DocVaultApiFactory factory) : IClassFixture<DocVaultApiFactory>
{
    [Fact]
    public async Task Authenticated_but_role_less_user_gets_403_not_401()
    {
        // The distinction matters: 401 tells a client "log in again", which sends the
        // user round a login loop that can never succeed. 403 says "you are logged in,
        // but you lack permission" — the honest answer. This is 'dave' in the seeded realm.
        var token = factory.Tokens.CreateToken(username: "dave", groups: ["/acme"]);

        var response = await factory.CreateClientWithToken(token).GetAsync("/documents");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reader_cannot_create_a_document()
    {
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.reader"], groups: ["/acme"]);

        var response = await factory.CreateClientWithToken(token)
            .PostAsJsonAsync("/documents", new { title = "Nope" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Editor_can_create_a_document()
    {
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.editor"], groups: ["/acme/engineering"]);

        var response = await factory.CreateClientWithToken(token)
            .PostAsJsonAsync("/documents", new { title = "Design doc" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Tenants_cannot_see_each_others_documents()
    {
        // The core multi-tenancy guarantee. Same endpoint, same roles, different tenant.
        var acme = factory.Tokens.CreateToken(apiRoles: ["doc.reader"], groups: ["/acme"]);
        var globex = factory.Tokens.CreateToken(apiRoles: ["doc.reader"], groups: ["/globex"]);

        var acmeDocs = await factory.CreateClientWithToken(acme).GetFromJsonAsync<DocumentsResponse>("/documents");
        var globexDocs = await factory.CreateClientWithToken(globex).GetFromJsonAsync<DocumentsResponse>("/documents");

        Assert.NotNull(acmeDocs);
        Assert.NotNull(globexDocs);
        Assert.Equal("acme", acmeDocs.Tenant);
        Assert.Equal("globex", globexDocs.Tenant);

        Assert.All(acmeDocs.Documents, d => Assert.Equal("acme", d.Tenant));
        Assert.All(globexDocs.Documents, d => Assert.Equal("globex", d.Tenant));
        Assert.DoesNotContain(globexDocs.Documents, d => acmeDocs.Documents.Any(a => a.Id == d.Id));
    }

    [Fact]
    public async Task Deleting_another_tenants_document_returns_404_not_403()
    {
        // 403 would confirm the document exists, which is the one fact tenant isolation
        // is meant to hide. Note the caller holds doc.admin, so the role check passes and
        // the request actually reaches the tenant comparison — this asserts the boundary,
        // not the policy. A 403 here would be the endpoint working and still leaking.
        var acmeEditor = factory.Tokens.CreateToken(apiRoles: ["doc.editor"], groups: ["/acme/engineering"]);
        var created = await factory.CreateClientWithToken(acmeEditor)
            .PostAsJsonAsync("/documents", new { title = "Acme merger terms" });
        var acmeDocument = await created.Content.ReadFromJsonAsync<DocumentDto>();
        Assert.NotNull(acmeDocument);

        var globexAdmin = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/globex"]);
        var response = await factory.CreateClientWithToken(globexAdmin)
            .DeleteAsync($"/documents/{acmeDocument.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The refusal must also be a genuine no-op. A 404 that deleted the document anyway
        // would be worse than a 403 — this proves it survived, and that the 404 was about
        // tenancy rather than the document being missing or the endpoint being broken.
        var acmeAdmin = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/acme"]);
        var ownTenant = await factory.CreateClientWithToken(acmeAdmin)
            .DeleteAsync($"/documents/{acmeDocument.Id}");

        Assert.Equal(HttpStatusCode.NoContent, ownTenant.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_document_is_indistinguishable_from_one_that_never_existed()
    {
        // The property that actually matters. Returning 404 is only useful if the two
        // cases are identical on the wire: the moment they differ — status, body, or
        // timing — the endpoint becomes an oracle for enumerating other tenants' ids.
        var acmeEditor = factory.Tokens.CreateToken(apiRoles: ["doc.editor"], groups: ["/acme/engineering"]);
        var created = await factory.CreateClientWithToken(acmeEditor)
            .PostAsJsonAsync("/documents", new { title = "Acme board minutes" });
        var acmeDocument = await created.Content.ReadFromJsonAsync<DocumentDto>();
        Assert.NotNull(acmeDocument);

        var globexAdmin = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/globex"]);
        var client = factory.CreateClientWithToken(globexAdmin);

        var existsElsewhere = await client.DeleteAsync($"/documents/{acmeDocument.Id}");
        var neverExisted = await client.DeleteAsync($"/documents/{Guid.NewGuid()}");

        Assert.Equal(neverExisted.StatusCode, existsElsewhere.StatusCode);
        Assert.Equal(
            await neverExisted.Content.ReadAsStringAsync(),
            await existsElsewhere.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/documents")]
    [InlineData("/analytics/usage")]
    public async Task A_user_in_two_tenants_gets_403_not_500(string path)
    {
        // The API refuses to guess which tenant a two-tenant caller means, and it must refuse
        // cleanly. An unhandled exception here was a 500: a server fault the client could
        // only retry, for what is really "this token is not scoped to one tenant".
        var token = factory.Tokens.CreateToken(
            apiRoles: ["doc.reader"], groups: ["/acme", "/globex"], scope: "openid analytics:read");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", "http://localhost:5173");
        var response = await factory.CreateClientWithToken(token).SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        // The exception handler clears the response before writing its own. If that took the
        // CORS headers with it, the SPA would see an opaque network error instead of this 403.
        Assert.Equal(
            "http://localhost:5173",
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Platform_admin_endpoint_requires_the_realm_role()
    {
        var docAdmin = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/acme"]);

        // doc.admin is a CLIENT role. It must not satisfy a REALM role requirement.
        var response = await factory.CreateClientWithToken(docAdmin).GetAsync("/admin/documents");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var platformAdmin = factory.Tokens.CreateToken(realmRoles: ["platform-admin"], groups: ["/acme"]);
        var allowed = await factory.CreateClientWithToken(platformAdmin).GetAsync("/admin/documents");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Analytics_requires_the_consented_scope_not_merely_a_role()
    {
        var withoutScope = factory.Tokens.CreateToken(apiRoles: ["doc.admin"], groups: ["/acme"]);
        var denied = await factory.CreateClientWithToken(withoutScope).GetAsync("/analytics/usage");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var withScope = factory.Tokens.CreateToken(
            apiRoles: ["doc.reader"], groups: ["/acme"], scope: "openid analytics:read");
        var allowed = await factory.CreateClientWithToken(withScope).GetAsync("/analytics/usage");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    private sealed record DocumentsResponse(string Tenant, string? Department, List<DocumentDto> Documents);
    private sealed record DocumentDto(Guid Id, string Tenant, string Title, string OwnerUsername, bool IsClassified);
}
