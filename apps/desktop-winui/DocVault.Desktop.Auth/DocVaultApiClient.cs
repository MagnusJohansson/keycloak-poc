// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocVault.Desktop.Auth;

/// <summary>Raised when the API demands a higher authentication level (RFC 9470).</summary>
public sealed class StepUpRequiredException(string requiredAcr)
    : Exception($"Step-up authentication required: acr_values={requiredAcr}")
{
    public string RequiredAcr { get; } = requiredAcr;
}

public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Calls the DocVault API with the caller's access token attached.</summary>
public sealed partial class DocVaultApiClient(HttpClient http)
{
    /// <summary>
    /// Parses the step-up challenge out of a <c>WWW-Authenticate</c> header.
    /// </summary>
    /// <remarks>
    /// Example the API sends:
    /// <code>
    /// Bearer error="insufficient_user_authentication", acr_values="silver"
    /// </code>
    /// Note this only works because the API explicitly exposes the header via CORS for browser
    /// clients; a desktop app is not subject to CORS, so it always sees it.
    /// </remarks>
    public static string? ParseStepUpChallenge(string? header)
    {
        if (string.IsNullOrEmpty(header) || !header.Contains("insufficient_user_authentication", StringComparison.Ordinal))
        {
            return null;
        }

        var match = AcrValuesPattern().Match(header);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The reason in an RFC 9457 problem body (<c>detail</c>, else <c>title</c>), or null when the
    /// body is empty or not a problem document, as for a role check's 403, which has no body.
    /// </summary>
    /// <remarks>
    /// A 403 is not always a missing role: "no tenant" and "ambiguous tenant" are 403s whose body
    /// says which, so the shell shows this rather than guessing. Each field is type-checked rather
    /// than cast, so a malformed body falls back instead of throwing.
    /// </remarks>
    public static string? ParseProblemReason(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in (ReadOnlySpan<string>)["detail", "title"])
            {
                if (document.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } reason)
                {
                    return reason;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<JsonElement> GetAsync(string accessToken, string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // A response carrying a challenge is recoverable: re-authenticating at a higher level
            // fixes it. RFC 9470 sends it as a 401; 403 is accepted too, since the header, not the
            // status, is what decides. A plain 403 is not recoverable — the user simply lacks the
            // role, and sending them back through sign-in would be a loop that can never succeed.
            var challenge = ParseStepUpChallenge(response.Headers.WwwAuthenticate.ToString());
            if (challenge is not null)
            {
                throw new StepUpRequiredException(challenge);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ApiException(response.StatusCode, string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "" : body);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // Plain escaped string: a raw literal ending in a quote needs an awkward delimiter here.
    [GeneratedRegex("acr_values=\"([^\"]+)\"")]
    private static partial Regex AcrValuesPattern();
}
