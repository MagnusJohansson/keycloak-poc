// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace DocVault.Api.Tests.Support;

/// <summary>
/// OpenFGA's HTTP API as far as the API uses it, minus the one thing that matters most:
/// it does NOT evaluate the model.
/// </summary>
/// <remarks>
/// <para>
/// A fake that evaluated docvault.fga would be a second implementation of the model, and
/// tests against it would test the fake. So checks are answered from a table each test
/// fills in (<see cref="Allow"/>), and everything else is a record of what the API sent.
/// These tests are about the API's wiring: which tuples it writes, which memberships it
/// sends, and what it answers for each decision. The model's meaning is tested on
/// OpenFGA's own engine (infra/openfga/docvault.fga.yml, `make test-model`), and the two
/// together against a real OpenFGA by tools/assert-fga.py.
/// </para>
/// <para>
/// It sits under the real <c>OpenFgaClient</c>, as its HttpMessageHandler, so the JSON
/// the API would send to OpenFGA is the JSON asserted on here.
/// </para>
/// </remarks>
public sealed class FakeOpenFga : HttpMessageHandler
{
    public const string StoreId = "01JTESTSTORE00000000000000";
    public const string ModelId = "01JTESTMODEL00000000000000";

    private readonly ConcurrentDictionary<(string User, string Relation, string Object), bool> _allowed = new();
    private readonly ConcurrentDictionary<(string User, string Relation, string Object), byte> _stored = new();
    private readonly ConcurrentQueue<JsonObject> _checks = new();
    private readonly ConcurrentQueue<JsonObject> _writes = new();

    /// <summary>Every request fails as if OpenFGA could not be reached.</summary>
    public volatile bool Down;

    /// <summary>Objects whose check comes back as an error rather than a decision.</summary>
    public ConcurrentDictionary<string, byte> Erroring { get; } = new();

    /// <summary>A write this says yes to fails as if OpenFGA had gone away mid-request.</summary>
    public Func<JsonObject, bool>? FailWrite { get; set; }

    /// <summary>The model id the store says it has. Anything else is 404.</summary>
    public string KnownModelId { get; set; } = ModelId;

    public void Allow(string user, string relation, string @object) => _allowed[(user, relation, @object)] = true;

    public static string User(string sub) => $"user:{sub}";

    public static string Document(Guid id) => $"document:{id:D}";

    /// <summary>Each check the API asked, with the contextual tuples it sent along.</summary>
    public IReadOnlyList<JsonObject> Checks => _checks.ToList();

    /// <summary>Each write request's body.</summary>
    public IReadOnlyList<JsonObject> Writes => _writes.ToList();

    /// <summary>Tuples written and not deleted, for one object.</summary>
    public IReadOnlyList<(string User, string Relation)> StoredFor(string @object) =>
        _stored.Keys.Where(k => k.Object == @object).Select(k => (k.User, k.Relation)).ToList();

    public IReadOnlyList<JsonObject> ChecksOf(string @object) =>
        Checks.Where(c => (string?)c["tuple_key"]?["object"] == @object).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("connection refused (FakeOpenFga.Down)");
        }

        var path = request.RequestUri!.AbsolutePath;
        Assert.StartsWith($"/stores/{StoreId}/", path);
        Assert.Equal("Bearer test-fga-key", request.Headers.Authorization?.ToString());

        var body = request.Content is null
            ? null
            : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();

        return path[$"/stores/{StoreId}/".Length..] switch
        {
            var p when p.StartsWith("authorization-models/", StringComparison.Ordinal) =>
                p == $"authorization-models/{KnownModelId}"
                    ? Json(new JsonObject { ["authorization_model"] = new JsonObject { ["id"] = KnownModelId } })
                    : Json(new JsonObject { ["code"] = "authorization_model_not_found" }, HttpStatusCode.NotFound),
            "batch-check" => BatchCheck(body!),
            "write" => Write(body!),
            "read" => Read(body!),
            var other => throw new InvalidOperationException($"FakeOpenFga has no {other}"),
        };
    }

    private HttpResponseMessage BatchCheck(JsonObject body)
    {
        Assert.Equal(ModelId, (string?)body["authorization_model_id"]);

        // Past OpenFGA's check cache, if it is ever switched on: with the default
        // consistency a revoked share stayed allowed until the cache entry expired.
        Assert.Equal("HIGHER_CONSISTENCY", (string?)body["consistency"]);
        var result = new JsonObject();
        foreach (var check in body["checks"]!.AsArray().Select(c => c!.AsObject()))
        {
            _checks.Enqueue(check.DeepClone().AsObject());
            var key = check["tuple_key"]!;
            var tuple = ((string)key["user"]!, (string)key["relation"]!, (string)key["object"]!);
            result[(string)check["correlation_id"]!] = Erroring.ContainsKey(tuple.Item3)
                ? new JsonObject { ["error"] = new JsonObject { ["message"] = "fake error" } }
                : new JsonObject { ["allowed"] = _allowed.ContainsKey(tuple) };
        }

        return Json(new JsonObject { ["result"] = result });
    }

    private HttpResponseMessage Write(JsonObject body)
    {
        if (FailWrite?.Invoke(body) == true)
        {
            throw new HttpRequestException("connection reset (FakeOpenFga.FailWrite)");
        }

        _writes.Enqueue(body.DeepClone().AsObject());
        foreach (var key in Keys(body["writes"]))
        {
            _stored[key] = 0;
        }

        foreach (var key in Keys(body["deletes"]))
        {
            _stored.TryRemove(key, out _);
        }

        return Json(new JsonObject());
    }

    private HttpResponseMessage Read(JsonObject body)
    {
        var @object = (string)body["tuple_key"]!["object"]!;
        var tuples = new JsonArray(StoredFor(@object)
            .Select(t => (JsonNode)new JsonObject
            {
                ["key"] = new JsonObject { ["user"] = t.User, ["relation"] = t.Relation, ["object"] = @object },
            })
            .ToArray());
        return Json(new JsonObject { ["tuples"] = tuples, ["continuation_token"] = "" });
    }

    private static IEnumerable<(string, string, string)> Keys(JsonNode? part) =>
        part?["tuple_keys"]?.AsArray().Select(k => ((string)k!["user"]!, (string)k["relation"]!, (string)k["object"]!)) ?? [];

    private static HttpResponseMessage Json(JsonObject body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

/// <summary>The people and departments a test says exist. Not Keycloak; nothing here is decided.</summary>
public sealed class FakeDirectory : DocVault.Api.Sharing.IPeopleDirectory
{
    private readonly ConcurrentDictionary<string, (Guid Id, string Tenant)> _people = new();
    private readonly ConcurrentDictionary<string, byte> _departments = new();

    public void AddPerson(string username, Guid id, string tenant) => _people[username] = (id, tenant);

    public void AddDepartment(string tenant, string department) => _departments[$"{tenant}/{department}"] = 0;

    /// <summary>How long a lookup of someone who exists takes; nobody is answered at once, as Keycloak does.</summary>
    public TimeSpan ExistingPersonDelay { get; set; } = TimeSpan.Zero;

    public async Task<Guid?> FindInTenantAsync(string username, string tenant, CancellationToken cancellationToken)
    {
        if (!_people.TryGetValue(username, out var p))
        {
            return null;
        }

        await Task.Delay(ExistingPersonDelay, cancellationToken);
        return p.Tenant == tenant ? p.Id : null;
    }

    public Task<bool> DepartmentExistsAsync(string tenant, string department, CancellationToken cancellationToken) =>
        Task.FromResult(_departments.ContainsKey($"{tenant}/{department}"));

    public Task<string?> UsernameOfAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_people.FirstOrDefault(p => p.Value.Id == userId).Key);
}
