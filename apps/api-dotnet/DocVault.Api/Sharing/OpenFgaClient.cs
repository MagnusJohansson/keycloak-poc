// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DocVault.Api.Sharing;

/// <summary>
/// The four OpenFGA calls the API makes, over its HTTP API.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than the OpenFGA SDK, on purpose. Four calls are little code,
/// and every failure here must become a 503 (<see cref="DependencyUnavailableException"/>)
/// within a few seconds, with no retry loop and no fallback in between. That is easier
/// to see, and to test, in code that has none to begin with.
/// </para>
/// <para>
/// No list-objects. It truncates silently: at OPENFGA_LIST_OBJECTS_MAX_RESULTS (1000 by
/// default) it returns HTTP 200 with the first results and no sign that there were more
/// (measured on v1.22.1). The store already knows a tenant's documents; OpenFGA is asked
/// about each of those instead.
/// </para>
/// </remarks>
public sealed class OpenFgaClient(HttpClient http, IOptions<SharingSettings> options, ILogger<OpenFgaClient> logger)
{
    /// <summary>OpenFGA refuses a batch-check of more (OPENFGA_MAX_CHECKS_PER_BATCH_CHECK; measured).</summary>
    public const int MaxChecksPerBatch = 50;

    /// <summary>OpenFGA's default limit on one write (OPENFGA_MAX_TUPLES_PER_WRITE).</summary>
    public const int MaxTuplesPerWrite = 100;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SharingSettings _settings = options.Value;

    /// <summary>Configures the HttpClient this class is given. Called from DI registration.</summary>
    public static void Configure(HttpClient client, SharingSettings settings)
    {
        client.BaseAddress = new Uri($"{settings.OpenFgaUrl!.TrimEnd('/')}/stores/{settings.StoreId}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenFgaApiKey);
        client.Timeout = Timeout;
    }

    /// <summary>
    /// One answer per check, in order. Each check carries the same contextual tuples: the
    /// caller's memberships (<see cref="Caller"/>).
    /// </summary>
    /// <remarks>
    /// HIGHER_CONSISTENCY on every check. With OpenFGA's check cache switched on, the default
    /// consistency answered "allowed" for a share revoked a moment before, until the cache
    /// entry expired (10 s by default; measured). The cache is off in this lab; this keeps a
    /// revoke immediate if someone switches it on.
    /// </remarks>
    public async Task<bool[]> BatchCheckAsync(IReadOnlyList<TupleKey> checks, IReadOnlyList<TupleKey> contextualTuples, CancellationToken cancellationToken)
    {
        var allowed = new bool[checks.Count];
        for (var start = 0; start < checks.Count; start += MaxChecksPerBatch)
        {
            var chunk = checks.Skip(start).Take(MaxChecksPerBatch).ToList();
            var request = new
            {
                authorization_model_id = _settings.ModelId,
                consistency = "HIGHER_CONSISTENCY",
                checks = chunk.Select((check, i) => new
                {
                    tuple_key = check,
                    contextual_tuples = new { tuple_keys = contextualTuples },
                    correlation_id = (start + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
            };

            using var response = await PostAsync("batch-check", request, cancellationToken);
            var results = response.RootElement.TryGetProperty("result", out var r) ? r : default;

            for (var i = 0; i < chunk.Count; i++)
            {
                var id = (start + i).ToString(System.Globalization.CultureInfo.InvariantCulture);

                // A per-check error (a relation the model lacks, a malformed id) is not "no".
                // Nothing was decided, so nothing is allowed and the request fails.
                if (results.ValueKind != JsonValueKind.Object
                    || !results.TryGetProperty(id, out var result)
                    || result.TryGetProperty("error", out _)
                    || !result.TryGetProperty("allowed", out var value)
                    || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    logger.LogError("OpenFGA gave no decision for check {Relation} on {Object}.", chunk[i].Relation, chunk[i].Object);
                    throw new DependencyUnavailableException("openfga", "no_decision");
                }

                allowed[start + i] = value.GetBoolean();
            }
        }

        return allowed;
    }

    /// <summary>
    /// Writes and deletes tuples. Both idempotent: writing a tuple that exists, or
    /// deleting one that does not, is not an error (on_duplicate / on_missing: ignore).
    /// </summary>
    /// <remarks>
    /// Split into writes of at most <see cref="MaxTuplesPerWrite"/>. Each is atomic, the
    /// whole is not: deleting a much-shared document can fail halfway. What remains is
    /// tuples for a document that still exists, and the delete can be retried.
    /// </remarks>
    public async Task WriteAsync(IReadOnlyList<TupleKey> writes, IReadOnlyList<TupleKey> deletes, CancellationToken cancellationToken)
    {
        foreach (var chunk in writes.Chunk(MaxTuplesPerWrite))
        {
            using var _ = await PostAsync("write", new
            {
                authorization_model_id = _settings.ModelId,
                writes = new { tuple_keys = chunk, on_duplicate = "ignore" },
            }, cancellationToken);
        }

        foreach (var chunk in deletes.Chunk(MaxTuplesPerWrite))
        {
            using var _ = await PostAsync("write", new
            {
                authorization_model_id = _settings.ModelId,
                deletes = new { tuple_keys = chunk, on_missing = "ignore" },
            }, cancellationToken);
        }
    }

    /// <summary>Every stored tuple of one object. Follows the continuation token to the end.</summary>
    public async Task<IReadOnlyList<TupleKey>> ReadAsync(string @object, CancellationToken cancellationToken)
    {
        var tuples = new List<TupleKey>();
        string? continuation = null;
        do
        {
            using var response = await PostAsync("read", new
            {
                tuple_key = new { @object },
                page_size = 100,
                continuation_token = continuation,
            }, cancellationToken);

            var root = response.RootElement;
            if (root.TryGetProperty("tuples", out var page) && page.ValueKind == JsonValueKind.Array)
            {
                foreach (var tuple in page.EnumerateArray())
                {
                    var key = tuple.GetProperty("key").Deserialize<TupleKey>(Json)
                        ?? throw new DependencyUnavailableException("openfga", "bad_read");
                    tuples.Add(key);
                }
            }

            continuation = root.TryGetProperty("continuation_token", out var token) ? token.GetString() : null;
        }
        while (!string.IsNullOrEmpty(continuation));

        return tuples;
    }

    /// <summary>
    /// Whether the store holds the model the API is pinned to. False for "not found",
    /// which means the configuration is wrong; an outage throws instead.
    /// </summary>
    public async Task<bool> ModelExistsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => http.GetAsync($"authorization-models/{Uri.EscapeDataString(_settings.ModelId!)}", cancellationToken),
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return false;
        }

        await EnsureSuccessAsync(response, "authorization-models", cancellationToken);
        return true;
    }

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => http.PostAsJsonAsync(path, body, Json, cancellationToken),
            cancellationToken);

        await EnsureSuccessAsync(response, path, cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new DependencyUnavailableException("openfga", "bad_response");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            return await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Refused, unreachable, or slower than Timeout. A caller hanging up is not an
            // outage and is let through as a cancellation.
            throw new DependencyUnavailableException("openfga", ex is TaskCanceledException ? "timeout" : "unreachable", ex);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // OpenFGA's error code is a fixed word of its own ("validation_error",
        // "store_id_not_found"), safe to log. Its message can quote the request.
        var code = "unknown";
        try
        {
            using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            code = error.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? code : code;
        }
        catch (JsonException)
        {
        }

        logger.LogError("OpenFGA refused {Path}: HTTP {Status} {Code}.", path, (int)response.StatusCode, code);
        throw new DependencyUnavailableException("openfga", $"http_{(int)response.StatusCode}");
    }
}
