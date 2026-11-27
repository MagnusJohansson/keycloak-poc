// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DocVault.Api.Domain;
using DocVault.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DocVault.Api.Tests.Security;

/// <summary>
/// The API's half of per-document authorization: what it sends OpenFGA, and what it does
/// with the answer. OpenFGA's answers here come from a table (see <see cref="FakeOpenFga"/>);
/// whether the MODEL gives the right answers is infra/openfga/docvault.fga.yml's job.
/// </summary>
public sealed class SharingTests(OpenFgaApiFactory factory) : IClassFixture<OpenFgaApiFactory>
{
    private static readonly Guid Roadmap = InMemoryDocumentStore.Seeded.AcmeRoadmap;
    private static readonly Guid Memo = InMemoryDocumentStore.Seeded.AcmeMemo;
    private static readonly Guid Suppliers = InMemoryDocumentStore.Seeded.GlobexSuppliers;

    private FakeOpenFga Fga => factory.Fga;

    // --- Membership comes from the token, and only from it ---------------------------

    [Fact]
    public async Task Every_check_carries_the_tokens_memberships_and_nothing_else()
    {
        var sub = NewSub();
        // "acme/sales" has no leading slash: not a group path, so not a department.
        var token = Token(sub, ["doc.reader"], "/acme/engineering", "/acme/legal", "acme/sales");

        await Client(token).GetAsync("/documents");

        var checks = Fga.Checks.Where(c => (string?)c["tuple_key"]!["user"] == FakeOpenFga.User(sub)).ToList();
        Assert.NotEmpty(checks);
        Assert.All(checks, check =>
        {
            Assert.Equal("can_view", (string?)check["tuple_key"]!["relation"]);
            Assert.Equal(
                [
                    $"user:{sub} member department:acme/engineering",
                    $"user:{sub} member department:acme/legal",
                    $"user:{sub} member tenant:acme",
                ],
                Contextual(check));
        });
    }

    [Fact]
    public async Task A_department_whose_name_could_not_be_an_id_grants_nothing()
    {
        // ':' and '#' mean something inside an OpenFGA tuple. Left out, not escaped.
        var sub = NewSub();
        await Client(Token(sub, ["doc.reader"], "/acme/le#gal", "/acme/a:b")).GetAsync("/documents");

        var check = Fga.Checks.First(c => (string?)c["tuple_key"]!["user"] == FakeOpenFga.User(sub));
        Assert.Equal([$"user:{sub} member tenant:acme"], Contextual(check));
    }

    [Fact]
    public async Task A_token_whose_subject_is_not_a_user_id_sees_nothing_and_owns_nothing()
    {
        var token = factory.Tokens.CreateToken(apiRoles: ["doc.editor"], groups: ["/acme"], sub: "service-account-x");
        var client = Client(token);

        var list = await client.GetFromJsonAsync<JsonObject>("/documents");
        Assert.Empty(list!["documents"]!.AsArray());

        var created = await client.PostAsJsonAsync("/documents", new { title = "orphan" });
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task The_tenant_in_the_written_tuples_is_the_tokens_not_the_bodys()
    {
        var sub = NewSub();
        var response = await Client(Token(sub, ["doc.editor"], "/acme"))
            .PostAsJsonAsync("/documents", new { title = "Mine", tenant = "globex", visibility = "private" });
        var id = await IdOf(response);

        Assert.Contains(("tenant:acme", "tenant"), Fga.StoredFor(FakeOpenFga.Document(id)));
        Assert.DoesNotContain(Fga.StoredFor(FakeOpenFga.Document(id)), t => t.User.Contains("globex"));
    }

    // --- Decisions --------------------------------------------------------------------

    [Fact]
    public async Task The_list_holds_what_OpenFGA_allows_and_nothing_more()
    {
        var sub = NewSub();
        Fga.Allow(FakeOpenFga.User(sub), "can_view", FakeOpenFga.Document(Roadmap));

        var list = await Client(Token(sub, ["doc.reader"], "/acme")).GetFromJsonAsync<JsonObject>("/documents");

        Assert.Equal([Roadmap.ToString("D")], list!["documents"]!.AsArray().Select(d => (string)d!["id"]!).ToList());
    }

    [Fact]
    public async Task A_document_you_may_not_see_is_a_404_identical_to_one_that_does_not_exist()
    {
        var sub = NewSub();
        var client = Client(Token(sub, ["doc.reader"], "/acme"));

        var hidden = await client.GetAsync($"/documents/{Roadmap}");
        var missing = await client.GetAsync($"/documents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(missing.StatusCode, hidden.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await hidden.Content.ReadAsStringAsync());

        Fga.Allow(FakeOpenFga.User(sub), "can_view", FakeOpenFga.Document(Roadmap));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/documents/{Roadmap}")).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_document_is_refused_before_OpenFGA_is_asked()
    {
        // The API's own tenant check. OpenFGA's model requires the tenant too; this proves
        // the boundary does not rest on OpenFGA alone. Even an "allow" would not get through.
        var sub = NewSub();
        Fga.Allow(FakeOpenFga.User(sub), "can_view", FakeOpenFga.Document(Suppliers));
        var client = Client(Token(sub, ["doc.admin"], "/acme"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/documents/{Suppliers}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/documents/{Suppliers}")).StatusCode);
        Assert.DoesNotContain(Fga.ChecksOf(FakeOpenFga.Document(Suppliers)), c => (string?)c["tuple_key"]!["user"] == FakeOpenFga.User(sub));
        Assert.NotNull(factory.Services.GetRequiredService<IDocumentStore>().Get(Suppliers));
    }

    [Fact]
    public async Task A_classified_document_is_not_reachable_by_id_whatever_OpenFGA_says()
    {
        // Classified documents sit behind the step-up policy on /classified.
        var sub = NewSub();
        Fga.Allow(FakeOpenFga.User(sub), "can_view", FakeOpenFga.Document(Memo));

        var response = await Client(Token(sub, ["doc.admin"], "/acme")).GetAsync($"/documents/{Memo}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_check_that_comes_back_as_an_error_is_a_503_not_a_no()
    {
        var sub = NewSub();
        var created = await Client(Token(sub, ["doc.editor"], "/acme")).PostAsJsonAsync("/documents", new { title = "x" });
        var id = await IdOf(created);
        Fga.Erroring[FakeOpenFga.Document(id)] = 0;
        try
        {
            var response = await Client(Token(sub, ["doc.reader"], "/acme")).GetAsync("/documents");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            Fga.Erroring.TryRemove(FakeOpenFga.Document(id), out _);
        }
    }

    // --- Writes -----------------------------------------------------------------------

    [Fact]
    public async Task A_private_document_records_its_tenant_and_owner_and_no_tenant_viewers()
    {
        var sub = NewSub();
        var id = await IdOf(await Client(Token(sub, ["doc.editor"], "/acme/engineering"))
            .PostAsJsonAsync("/documents", new { title = "Private", visibility = "private" }));

        Assert.Equal(
            [("tenant:acme", "tenant"), ($"user:{sub}", "owner")],
            Fga.StoredFor(FakeOpenFga.Document(id)).OrderBy(t => t.Relation == "owner").ToList());
    }

    [Fact]
    public async Task IsOwner_is_true_for_the_owner_only_and_never_for_a_seeded_document()
    {
        var (owner, id) = await PrivateDocumentAsync();
        Fga.Allow(FakeOpenFga.User(owner), "can_view", FakeOpenFga.Document(Roadmap));
        var other = NewSub();
        Fga.Allow(FakeOpenFga.User(other), "can_view", FakeOpenFga.Document(id));

        var mine = await Client(Token(owner, ["doc.editor"], "/acme")).GetFromJsonAsync<JsonObject>("/documents");
        var theirs = await Client(Token(other, ["doc.reader"], "/acme")).GetFromJsonAsync<JsonObject>($"/documents/{id}");

        bool IsOwner(JsonObject list, Guid doc) => (bool)list["documents"]!.AsArray().Single(d => (string)d!["id"]! == doc.ToString("D"))!["isOwner"]!;
        Assert.True(IsOwner(mine!, id));
        Assert.False(IsOwner(mine!, Roadmap));
        Assert.False((bool)theirs!["isOwner"]!);
        Assert.Null(theirs["ownerId"]);
    }

    [Fact]
    public async Task A_document_is_visible_to_its_tenant_unless_made_private()
    {
        var sub = NewSub();
        var response = await Client(Token(sub, ["doc.editor"], "/acme"))
            .PostAsJsonAsync("/documents", new { title = "Shared by default" });
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal("tenant", (string?)body!["visibility"]);
        Assert.Contains(("tenant:acme#member", "viewer"), Fga.StoredFor(FakeOpenFga.Document(Guid.Parse((string)body["id"]!))));
    }

    [Fact]
    public async Task Without_OpenFGA_nothing_is_decided_and_nothing_is_half_made()
    {
        var sub = NewSub();
        var client = Client(Token(sub, ["doc.editor"], "/acme"));
        var before = factory.Services.GetRequiredService<IDocumentStore>().ListForTenant("acme", true).Count;

        Fga.Down = true;
        try
        {
            // Not 200 with an empty list, which reads as "you have no documents".
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/documents")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/documents/{Roadmap}")).StatusCode);

            var created = await client.PostAsJsonAsync("/documents", new { title = "During the outage" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, created.StatusCode);
            Assert.DoesNotContain("outage", await created.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Fga.Down = false;
        }

        // The document is not left in the store without its tuples, invisible to everyone.
        Assert.Equal(before, factory.Services.GetRequiredService<IDocumentStore>().ListForTenant("acme", true).Count);
    }

    // --- Sharing ----------------------------------------------------------------------

    [Fact]
    public async Task A_share_records_the_persons_user_id_not_their_username()
    {
        var (owner, id) = await PrivateDocumentAsync();
        var carolId = Guid.NewGuid();
        factory.Directory.AddPerson("carol", carolId, "acme");

        var response = await Client(Token(owner, ["doc.editor"], "/acme"))
            .PostAsJsonAsync($"/documents/{id}/shares", new { username = "Carol" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(($"user:{carolId:D}", "viewer"), Fga.StoredFor(FakeOpenFga.Document(id)));
        Assert.DoesNotContain(Fga.StoredFor(FakeOpenFga.Document(id)), t => t.User.Contains("carol"));
    }

    [Fact]
    public async Task Someone_in_another_tenant_cannot_be_shared_with_and_looks_like_nobody()
    {
        // The model would make such a share grant nothing, but it would switch on the day
        // that person joined acme. And the answer must not tell alice who works at globex.
        var (owner, id) = await PrivateDocumentAsync();
        factory.Directory.AddPerson("frank", Guid.NewGuid(), "globex");
        var client = Client(Token(owner, ["doc.editor"], "/acme"));
        var writesBefore = Fga.Writes.Count;

        var elsewhere = await client.PostAsJsonAsync($"/documents/{id}/shares", new { username = "frank" });
        var nobody = await client.PostAsJsonAsync($"/documents/{id}/shares", new { username = "no-such-person" });

        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
        Assert.Equal(Without(await nobody.Content.ReadAsStringAsync(), "traceId"), Without(await elsewhere.Content.ReadAsStringAsync(), "traceId"));
        Assert.Equal(writesBefore, Fga.Writes.Count);
    }

    [Fact]
    public async Task A_department_share_names_the_department_of_the_documents_tenant()
    {
        var (owner, id) = await PrivateDocumentAsync();
        factory.Directory.AddDepartment("acme", "legal");
        var client = Client(Token(owner, ["doc.editor"], "/acme"));

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/documents/{id}/shares", new { department = "legal" })).StatusCode);
        Assert.Contains(("department:acme/legal#member", "viewer"), Fga.StoredFor(FakeOpenFga.Document(id)));

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/documents/{id}/shares", new { department = "sales" })).StatusCode);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"username":"carol","department":"legal"}""")]
    [InlineData("""{"department":"le#gal"}""")]
    [InlineData("""{"username":"car\nol"}""")]
    [InlineData("""{"username":""}""")]
    [InlineData("""{"department":"."}""")]
    [InlineData("""{"department":".."}""")]
    [InlineData("""{"department":"-legal"}""")]
    public async Task A_share_names_exactly_one_person_or_department(string body)
    {
        var (owner, id) = await PrivateDocumentAsync();
        var response = await Client(Token(owner, ["doc.editor"], "/acme"))
            .PostAsync($"/documents/{id}/shares", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_shares_and_someone_who_cannot_see_the_document_gets_404()
    {
        var (_, id) = await PrivateDocumentAsync();
        var viewer = NewSub();
        Fga.Allow(FakeOpenFga.User(viewer), "can_view", FakeOpenFga.Document(id));
        factory.Directory.AddPerson("erin", Guid.NewGuid(), "acme");

        var seeing = await Client(Token(viewer, ["doc.admin"], "/acme"))
            .PostAsJsonAsync($"/documents/{id}/shares", new { username = "erin" });
        var notSeeing = await Client(Token(NewSub(), ["doc.admin"], "/acme"))
            .PostAsJsonAsync($"/documents/{id}/shares", new { username = "erin" });

        Assert.Equal(HttpStatusCode.Forbidden, seeing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notSeeing.StatusCode);
    }

    [Fact]
    public async Task Shares_are_listed_by_current_username_and_revoked_by_id()
    {
        var (owner, id) = await PrivateDocumentAsync();
        var graceId = Guid.NewGuid();
        factory.Directory.AddPerson("grace", graceId, "acme");
        var client = Client(Token(owner, ["doc.editor"], "/acme"));
        await client.PostAsJsonAsync($"/documents/{id}/shares", new { username = "grace" });

        var listed = await client.GetFromJsonAsync<JsonObject>($"/documents/{id}/shares");
        var share = Assert.Single(listed!["shares"]!.AsArray());
        Assert.Equal("grace", (string?)share!["username"]);
        Assert.Equal(graceId.ToString("D"), (string?)share["id"]);
        Assert.True((bool)share["inTenant"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/documents/{id}/shares/users/{graceId}")).StatusCode);
        Assert.DoesNotContain(($"user:{graceId:D}", "viewer"), Fga.StoredFor(FakeOpenFga.Document(id)));
    }

    [Fact]
    public async Task Someone_who_has_left_the_tenant_is_listed_as_such_and_can_still_be_removed()
    {
        // The share stays in OpenFGA and grants nothing while they are elsewhere. Listed
        // without a mark, it read as access they still had (found by the second review).
        var (owner, id) = await PrivateDocumentAsync();
        var heidiId = Guid.NewGuid();
        factory.Directory.AddPerson("heidi", heidiId, "acme");
        var client = Client(Token(owner, ["doc.editor"], "/acme"));
        await client.PostAsJsonAsync($"/documents/{id}/shares", new { username = "heidi" });

        factory.Directory.AddPerson("heidi", heidiId, "globex");

        var listed = await client.GetFromJsonAsync<JsonObject>($"/documents/{id}/shares");
        var share = Assert.Single(listed!["shares"]!.AsArray());
        Assert.Equal("heidi", (string?)share!["username"]);
        Assert.False((bool)share["inTenant"]!);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/documents/{id}/shares/users/{heidiId}")).StatusCode);
        Assert.DoesNotContain(($"user:{heidiId:D}", "viewer"), Fga.StoredFor(FakeOpenFga.Document(id)));
    }

    // --- Delete -----------------------------------------------------------------------

    [Fact]
    public async Task An_administrator_cannot_delete_a_private_document_they_cannot_see()
    {
        var (_, id) = await PrivateDocumentAsync();

        var response = await Client(Token(NewSub(), ["doc.admin"], "/acme")).DeleteAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(factory.Services.GetRequiredService<IDocumentStore>().Get(id));
        Assert.NotEmpty(Fga.StoredFor(FakeOpenFga.Document(id)));
    }

    [Fact]
    public async Task The_owner_deletes_their_own_document_and_its_relationships_go_with_it()
    {
        var (owner, id) = await PrivateDocumentAsync();
        Fga.Allow(FakeOpenFga.User(owner), "can_delete", FakeOpenFga.Document(id));

        // An editor, not an administrator: ownership is the right here.
        var response = await Client(Token(owner, ["doc.editor"], "/acme")).DeleteAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<IDocumentStore>().Get(id));
        Assert.Empty(Fga.StoredFor(FakeOpenFga.Document(id)));
    }

    [Fact]
    public async Task A_delete_that_fails_halfway_leaves_the_owner_able_to_try_again()
    {
        // More shares than one write holds, and OpenFGA failing on the second write. In
        // one pass the first write took the owner and tenant with it: a document nobody
        // could see, so nobody could delete it (found by review, on a real OpenFGA).
        var (owner, id) = await PrivateDocumentAsync();
        var shares = Enumerable.Range(0, 120)
            .Select(_ => new { user = $"user:{Guid.NewGuid():D}", relation = "viewer", @object = FakeOpenFga.Document(id) })
            .ToArray();
        await WriteDirectlyAsync(shares);
        Fga.Allow(FakeOpenFga.User(owner), "can_delete", FakeOpenFga.Document(id));
        var client = Client(Token(owner, ["doc.editor"], "/acme"));

        var deletes = 0;
        Fga.FailWrite = body => body["deletes"] is not null && ++deletes == 2;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.DeleteAsync($"/documents/{id}")).StatusCode);
        }
        finally
        {
            Fga.FailWrite = null;
        }

        Assert.Contains(($"user:{owner}", "owner"), Fga.StoredFor(FakeOpenFga.Document(id)));
        Assert.Contains(("tenant:acme", "tenant"), Fga.StoredFor(FakeOpenFga.Document(id)));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/documents/{id}")).StatusCode);
        Assert.Empty(Fga.StoredFor(FakeOpenFga.Document(id)));
    }

    [Fact]
    public async Task A_document_whose_tuples_fit_in_one_write_is_deleted_in_one_so_a_failure_changes_nothing()
    {
        // Two writes, shares then tenant and owner, failing at the second: the tenant-wide
        // visibility was already gone, and an administrator could no longer see the
        // document to delete it again (found by the second review).
        var (owner, id) = await TenantDocumentAsync();
        await WriteDirectlyAsync([new { user = $"user:{Guid.NewGuid():D}", relation = "viewer", @object = FakeOpenFga.Document(id) }]);
        var before = Fga.StoredFor(FakeOpenFga.Document(id)).Order().ToList();
        Fga.Allow(FakeOpenFga.User(owner), "can_delete", FakeOpenFga.Document(id));
        var client = Client(Token(owner, ["doc.editor"], "/acme"));

        Fga.FailWrite = DeletesTheTenant;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.DeleteAsync($"/documents/{id}")).StatusCode);
        }
        finally
        {
            Fga.FailWrite = null;
        }

        Assert.Equal(before, Fga.StoredFor(FakeOpenFga.Document(id)).Order().ToList());

        var writes = Fga.Writes.Count;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/documents/{id}")).StatusCode);
        Assert.Equal(writes + 1, Fga.Writes.Count);
        Assert.Empty(Fga.StoredFor(FakeOpenFga.Document(id)));
    }

    [Fact]
    public async Task A_large_delete_that_fails_at_the_end_keeps_everything_that_lets_anyone_see_the_document()
    {
        // Too many tuples for one write. The last write fails: the shares are gone, and the
        // tenant, the owner and the tenant-wide visibility must all still be there, so that
        // the administrator who sees it through the tenant can try again, not only the owner.
        var (owner, id) = await TenantDocumentAsync();
        await WriteDirectlyAsync(Enumerable.Range(0, 120)
            .Select(_ => (object)new { user = $"user:{Guid.NewGuid():D}", relation = "viewer", @object = FakeOpenFga.Document(id) })
            .ToArray());
        Fga.Allow(FakeOpenFga.User(owner), "can_delete", FakeOpenFga.Document(id));

        Fga.FailWrite = DeletesTheTenant;
        try
        {
            var response = await Client(Token(owner, ["doc.editor"], "/acme")).DeleteAsync($"/documents/{id}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            Fga.FailWrite = null;
        }

        (string, string)[] visibility = [("tenant:acme", "tenant"), ("tenant:acme#member", "viewer"), ($"user:{owner}", "owner")];
        Assert.Equal(visibility.Order(), Fga.StoredFor(FakeOpenFga.Document(id)).Order());

        var admin = NewSub();
        Fga.Allow(FakeOpenFga.User(admin), "can_view", FakeOpenFga.Document(id));
        Assert.Equal(HttpStatusCode.NoContent,
            (await Client(Token(admin, ["doc.admin"], "/acme")).DeleteAsync($"/documents/{id}")).StatusCode);
        Assert.Empty(Fga.StoredFor(FakeOpenFga.Document(id)));
    }

    [Fact]
    public async Task A_large_delete_that_fails_at_the_end_keeps_what_lets_the_administrator_deleting_it_see_it()
    {
        // A private document whose owner has left, and an administrator who sees it only
        // through a share: to them, and to the two departments they are in. The last write
        // fails. With only tenant and owner kept, the first write took those shares and
        // nobody could see the document any more, so nobody could delete it (found by the
        // third review).
        //
        // The fake answers checks from its table, not from the stored tuples, so the
        // retry's 204 does not show that sight survived: the StoredFor assertion does.
        // That those tuples give the administrator can_view is the model's business, and
        // assert-fga checks it against the real OpenFGA.
        var (owner, id) = await PrivateDocumentAsync();
        var admin = NewSub();
        var document = FakeOpenFga.Document(id);
        await WriteDirectlyAsync(Enumerable.Range(0, 120)
            .Select(_ => (object)new { user = $"user:{Guid.NewGuid():D}", relation = "viewer", @object = document })
            .Append(new { user = $"user:{admin}", relation = "viewer", @object = document })
            .Append(new { user = "department:acme/legal#member", relation = "viewer", @object = document })
            .Append(new { user = "department:acme/finance#member", relation = "viewer", @object = document })
            .Append(new { user = "department:acme/engineering#member", relation = "viewer", @object = document })
            .ToArray());
        Fga.Allow(FakeOpenFga.User(admin), "can_view", document);
        var client = Client(Token(admin, ["doc.admin"], "/acme/legal", "/acme/finance"));

        Fga.FailWrite = DeletesTheTenant;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.DeleteAsync($"/documents/{id}")).StatusCode);
        }
        finally
        {
            Fga.FailWrite = null;
        }

        // Not engineering's share: the administrator is not in engineering.
        (string, string)[] kept =
        [
            ("tenant:acme", "tenant"), ($"user:{owner}", "owner"),
            ($"user:{admin}", "viewer"), ("department:acme/legal#member", "viewer"),
            ("department:acme/finance#member", "viewer"),
        ];
        Assert.Equal(kept.Order(), Fga.StoredFor(document).Order());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/documents/{id}")).StatusCode);
        Assert.Empty(Fga.StoredFor(document));
    }

    [Fact]
    public async Task Deleting_a_classified_document_without_doc_admin_is_a_404_like_any_unknown_id()
    {
        // A 403 here confirmed that a document behind the step-up policy exists.
        var sub = NewSub();
        Fga.Allow(FakeOpenFga.User(sub), "can_view", FakeOpenFga.Document(Memo));
        var client = Client(Token(sub, ["doc.editor"], "/acme"));

        var classified = await client.DeleteAsync($"/documents/{Memo}");
        var unknown = await client.DeleteAsync($"/documents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, classified.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await classified.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Seeing_a_document_is_not_enough_to_delete_it()
    {
        var sub = NewSub();
        Fga.Allow(FakeOpenFga.User(sub), "can_view", FakeOpenFga.Document(Roadmap));

        var response = await Client(Token(sub, ["doc.editor"], "/acme")).DeleteAsync($"/documents/{Roadmap}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>A private document, owned by a new person who can see and share it.</summary>
    private async Task<(string Owner, Guid Id)> PrivateDocumentAsync()
    {
        var owner = NewSub();
        var id = await IdOf(await Client(Token(owner, ["doc.editor"], "/acme"))
            .PostAsJsonAsync("/documents", new { title = "Private", visibility = "private" }));
        Fga.Allow(FakeOpenFga.User(owner), "can_view", FakeOpenFga.Document(id));
        Fga.Allow(FakeOpenFga.User(owner), "can_share", FakeOpenFga.Document(id));
        return (owner, id);
    }

    /// <summary>A tenant-wide document, owned by a new person who can see it.</summary>
    private async Task<(string Owner, Guid Id)> TenantDocumentAsync()
    {
        var owner = NewSub();
        var id = await IdOf(await Client(Token(owner, ["doc.editor"], "/acme"))
            .PostAsJsonAsync("/documents", new { title = "For everyone", visibility = "tenant" }));
        Fga.Allow(FakeOpenFga.User(owner), "can_view", FakeOpenFga.Document(id));
        return (owner, id);
    }

    /// <summary>A write that deletes a document's tenant tuple: in a split delete, the last one.</summary>
    private static bool DeletesTheTenant(JsonObject body) =>
        body["deletes"]?["tuple_keys"]?.AsArray().Any(t => (string?)t!["relation"] == "tenant") == true;

    private static string NewSub() => Guid.NewGuid().ToString("D");

    /// <summary>Tuples straight into the fake, as if written earlier.</summary>
    private async Task WriteDirectlyAsync(object[] tuples)
    {
        using var http = new HttpClient(Fga, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://fga.test/stores/{FakeOpenFga.StoreId}/write")
        {
            Content = JsonContent.Create(new { writes = new { tuple_keys = tuples } }),
        };
        request.Headers.Authorization = new("Bearer", "test-fga-key");
        (await http.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private string Token(string sub, string[] roles, params string[] groups) =>
        factory.Tokens.CreateToken(apiRoles: roles, groups: groups, sub: sub);

    private HttpClient Client(string token) => factory.CreateClientWithToken(token);

    private static async Task<Guid> IdOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return Guid.Parse((string)body!["id"]!);
    }

    private static List<string> Contextual(JsonObject check) =>
        check["contextual_tuples"]!["tuple_keys"]!.AsArray()
            .Select(t => $"{t!["user"]} {t["relation"]} {t["object"]}")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Without(string json, string property)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove(property);
        return node.ToJsonString();
    }
}
