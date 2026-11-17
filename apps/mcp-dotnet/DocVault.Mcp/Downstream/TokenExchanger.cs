// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Text.Json;

namespace DocVault.Mcp.Downstream;

/// <summary>
/// Swaps the token a client presented to this server for one aimed at the DocVault
/// API (OAuth 2.0 Token Exchange, RFC 8693).
///
/// The incoming token must never be forwarded: its audience is this server, so the
/// API would refuse it, and an API that did accept it would be accepting a token a
/// client obtained for something else (the "token passthrough" MCP forbids). The
/// exchanged token is still the user's: same subject, their roles, their tenant
/// group. Keycloak decides what it carries (see mcp.tf: document roles only).
/// </summary>
public interface ITokenExchanger
{
    Task<string> ExchangeForApiAsync(string subjectToken, CancellationToken cancellationToken);
}

/// <param name="refused">True when Keycloak said no (4xx); false when it failed to answer properly.</param>
public sealed class TokenExchangeException(string message, bool refused = false) : Exception(message)
{
    public bool Refused { get; } = refused;
}

public sealed class KeycloakTokenExchanger(HttpClient http, McpSettings settings, ILogger<KeycloakTokenExchanger> logger)
    : ITokenExchanger
{
    public const string HttpClientName = "keycloak-token";

    public async Task<string> ExchangeForApiAsync(string subjectToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                // The client_id is the resource URL; see mcp.tf.
                ["client_id"] = settings.Resource,
                ["client_secret"] = settings.ClientSecret,
                ["subject_token"] = subjectToken,
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["requested_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["audience"] = settings.ApiAudience,
            }),
        };

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Keycloak's reason is for the operator, not the assistant: the log gets it,
            // the caller gets a fixed sentence.
            logger.LogWarning("Token exchange refused: {Status} {Body}", (int)response.StatusCode, Truncate(body));
            throw new TokenExchangeException(
                "DocVault could not issue an API token for this request.",
                refused: (int)response.StatusCode is >= 400 and < 500);
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("access_token").GetString()
                ?? throw new TokenExchangeException("The token endpoint returned no access token.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Token exchange returned an unreadable response.");
            throw new TokenExchangeException("DocVault could not issue an API token for this request.");
        }
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];
}
