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

    public async Task<JsonElement> GetAsync(string accessToken, string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // A 403 carrying a challenge is recoverable: re-authenticating at a higher level
            // fixes it. A plain 403 is not — the user simply lacks the role, and sending them
            // back through sign-in would be a loop that can never succeed.
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
