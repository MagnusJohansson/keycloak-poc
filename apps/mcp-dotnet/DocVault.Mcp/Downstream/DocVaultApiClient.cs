// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace DocVault.Mcp.Downstream;

/// <summary>The outcome of an API call, in terms a tool can report.</summary>
/// <param name="Reason">Why it was not Ok, as a fixed word for the audit log.</param>
public sealed record ApiResult(bool Ok, string Text, string? Reason = null);

/// <summary>
/// Calls the DocVault API as the signed-in user, with an exchanged token. The API
/// stays the authority on what the user may do; this client only reports its answer.
/// </summary>
public sealed class DocVaultApiClient(HttpClient http, ITokenExchanger exchanger)
{
    public const string HttpClientName = "docvault-api";

    public Task<ApiResult> ListDocumentsAsync(string userToken, CancellationToken ct) =>
        SendAsync(userToken, HttpMethod.Get, "/documents", null, ct);

    public Task<ApiResult> CreateDocumentAsync(string userToken, string title, CancellationToken ct) =>
        SendAsync(userToken, HttpMethod.Post, "/documents", JsonContent.Create(new { title }), ct);

    public Task<ApiResult> DeleteDocumentAsync(string userToken, Guid id, CancellationToken ct) =>
        SendAsync(userToken, HttpMethod.Delete, $"/documents/{id}", null, ct);

    // Sharing: the API's 404 and 403 mean something narrower here, and say so.
    private const string ShareNotFound = "No such document, person or department in your tenant.";
    private const string ShareForbidden = "DocVault refused: only a document's owner can share it, or stop sharing it.";

    public Task<ApiResult> ShareAsync(string userToken, Guid id, string? username, string? department, CancellationToken ct) =>
        SendAsync(userToken, HttpMethod.Post, $"/documents/{id}/shares", JsonContent.Create(new { username, department }), ct,
            ShareNotFound, ShareForbidden);

    public Task<ApiResult> ListSharesAsync(string userToken, Guid id, CancellationToken ct) =>
        SendAsync(userToken, HttpMethod.Get, $"/documents/{id}/shares", null, ct, ShareNotFound, ShareForbidden);

    public Task<ApiResult> UnsharePersonAsync(string userToken, Guid id, Guid userId, CancellationToken ct) =>
        SendAsync(userToken, HttpMethod.Delete, $"/documents/{id}/shares/users/{userId}", null, ct,
            ShareNotFound, ShareForbidden, done: "No longer shared with that person.");

    public Task<ApiResult> UnshareDepartmentAsync(string userToken, Guid id, string department, CancellationToken ct) =>
        SendAsync(userToken, HttpMethod.Delete, $"/documents/{id}/shares/departments/{Uri.EscapeDataString(department)}", null, ct,
            ShareNotFound, ShareForbidden, done: "No longer shared with that department.");

    private async Task<ApiResult> SendAsync(
        string userToken, HttpMethod method, string path, HttpContent? content, CancellationToken ct,
        string notFound = "No such document in your tenant.",
        string forbidden = "DocVault refused: your account is not allowed to do this. The assistant's permission is not the problem; your DocVault role is.",
        string done = "Deleted.")
    {
        string apiToken;
        try
        {
            apiToken = await exchanger.ExchangeForApiAsync(userToken, ct);
        }
        catch (TokenExchangeException ex)
        {
            return new ApiResult(false, ex.Message, ex.Refused ? "exchange_refused" : "exchange_failed");
        }

        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        return response.StatusCode switch
        {
            HttpStatusCode.NoContent => new ApiResult(true, done),
            >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => new ApiResult(true, body),
            // The API's own distinctions, kept: 403 = signed in but not allowed;
            // 404 = no such document in your tenant (never "exists elsewhere").
            HttpStatusCode.Forbidden => new ApiResult(false, forbidden, "api_forbidden"),
            HttpStatusCode.NotFound => new ApiResult(false, notFound, "not_found"),
            HttpStatusCode.Unauthorized => new ApiResult(false, "DocVault did not accept the API token.", "api_unauthorized"),
            // The API could not ask OpenFGA (or Keycloak) who may see what. Nothing was decided.
            HttpStatusCode.ServiceUnavailable => new ApiResult(false,
                "DocVault could not check who may see which document just now. Nothing was decided; try again shortly.", "api_unavailable"),
            _ => new ApiResult(false, $"DocVault returned {(int)response.StatusCode}.", $"api_status_{(int)response.StatusCode}"),
        };
    }
}
