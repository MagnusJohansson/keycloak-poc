// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Text;

namespace DocVault.Mcp.Tests.Support;

/// <summary>
/// Stands in for both things the server calls: Keycloak's token endpoint and the
/// DocVault API. It records every request, so a test can assert which token went
/// where: the point of the exchange is that the API never sees the incoming one.
/// </summary>
public sealed class DownstreamStub : HttpMessageHandler
{
    public const string ExchangedToken = "exchanged-token-for-the-api";

    public sealed record Seen(string Method, string Url, string? Authorization, string Body);

    private readonly List<Seen> _requests = [];

    public IReadOnlyList<Seen> Requests
    {
        get { lock (_requests) { return [.. _requests]; } }
    }

    public IEnumerable<Seen> TokenRequests => Requests.Where(r => r.Url.Contains("/protocol/openid-connect/token"));
    public IEnumerable<Seen> ApiRequests => Requests.Where(r => !r.Url.Contains("/protocol/openid-connect/token"));

    /// <summary>What the token endpoint answers. Default: a successful exchange.</summary>
    public (HttpStatusCode Status, string Body) Exchange { get; set; } =
        (HttpStatusCode.OK, $$"""{"access_token":"{{ExchangedToken}}","token_type":"Bearer","expires_in":300}""");

    /// <summary>What the API answers. Default: one document.</summary>
    public (HttpStatusCode Status, string Body) Api { get; set; } =
        (HttpStatusCode.OK, """{"tenant":"acme","documents":[{"id":"7b0c8f0e-4d0a-4c46-9f0a-1f2f3a4b5c6d","title":"Acme Q3 Roadmap"}]}""");

    /// <summary>
    /// When set, the API does not answer until this completes: the request has
    /// arrived and the caller is still waiting.
    /// </summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Completes when the API has received a request.</summary>
    public TaskCompletionSource ApiReached { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Reset()
    {
        lock (_requests) { _requests.Clear(); }
        ApiReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var seen = new Seen(request.Method.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body);
        lock (_requests) { _requests.Add(seen); }

        var isExchange = seen.Url.Contains("/protocol/openid-connect/token");
        if (!isExchange)
        {
            ApiReached.TrySetResult();
            if (Hold is { } hold)
            {
                // The API has the request and acts on it whether or not the caller waits.
                await hold.Task.WaitAsync(cancellationToken);
            }
        }

        var (status, content) = isExchange ? Exchange : Api;
        return new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
