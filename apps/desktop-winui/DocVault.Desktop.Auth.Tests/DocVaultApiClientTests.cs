// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using DocVault.Desktop.Auth;

namespace DocVault.Desktop.Auth.Tests;

/// <summary>
/// How <see cref="DocVaultApiClient.GetAsync"/> classifies a refusal. <see cref="StepUpChallengeTests"/>
/// covers the header parser; these cover the status handling around it, which the parser tests
/// cannot see — reverting the 401 branch to 403-only would leave every one of those green.
/// </summary>
public class DocVaultApiClientTests
{
    private const string StepUpChallenge =
        """Bearer error="insufficient_user_authentication", error_description="A higher authentication level is required", acr_values="silver" """;

    [Fact]
    public async Task A_401_step_up_challenge_raises_StepUpRequiredException()
    {
        // RFC 9470 sends the challenge as a 401. It must be recognised before anything treats
        // the 401 as an expired session.
        var client = ClientReturning(HttpStatusCode.Unauthorized, wwwAuthenticate: StepUpChallenge);

        var ex = await Assert.ThrowsAsync<StepUpRequiredException>(() => client.GetAsync("token", "/documents/classified"));

        Assert.Equal("silver", ex.RequiredAcr);
    }

    [Fact]
    public async Task An_ordinary_401_is_not_mistaken_for_step_up()
    {
        var client = ClientReturning(HttpStatusCode.Unauthorized, wwwAuthenticate: """Bearer error="invalid_token" """);

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetAsync("token", "/documents"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
    }

    [Fact]
    public async Task A_plain_403_keeps_the_servers_reason()
    {
        // A 403 is not always a missing role: "no tenant" and "ambiguous tenant" are 403s with a
        // problem body saying which. The client must pass that on, not replace it with a guess.
        const string problem = """{"title":"Ambiguous tenant","status":403,"detail":"Your account belongs to more than one tenant; a tenant-scoped token is required."}""";
        var client = ClientReturning(HttpStatusCode.Forbidden, body: problem);

        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetAsync("token", "/documents"));

        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
        Assert.Contains("Ambiguous tenant", ex.Message);
    }

    private static DocVaultApiClient ClientReturning(HttpStatusCode status, string? wwwAuthenticate = null, string body = "")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        if (wwwAuthenticate is not null)
        {
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", wwwAuthenticate);
        }

        return new DocVaultApiClient(new HttpClient(new StubHandler(response)) { BaseAddress = new Uri("http://localhost:5001") });
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
