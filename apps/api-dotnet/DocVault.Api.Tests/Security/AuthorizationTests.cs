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
