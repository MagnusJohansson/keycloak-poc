// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace DocVault.Mcp.Audit;

public static class TokenRefusal
{
    /// <summary>
    /// Why a token was refused, as a fixed word. The exception's message is not
    /// used: it can quote parts of the token.
    /// </summary>
    public static string Reason(Exception exception) => exception switch
    {
        SecurityTokenExpiredException => "expired",
        SecurityTokenNotYetValidException => "not_yet_valid",
        SecurityTokenInvalidAudienceException => "invalid_audience",
        SecurityTokenInvalidIssuerException => "invalid_issuer",
        SecurityTokenInvalidAlgorithmException => "invalid_algorithm",
        SecurityTokenSignatureKeyNotFoundException => "invalid_signature",
        SecurityTokenInvalidSignatureException => "invalid_signature",
        SecurityTokenMalformedException => "malformed",
        _ => "invalid",
    };

    /// <summary>
    /// True when this server has no signing keys to check a token against, because
    /// it could not fetch them from the issuer. A token refused then is not known to
    /// be forged, and the record must not read as if it were.
    /// </summary>
    public static async Task<bool> KeysUnavailableAsync(JwtBearerOptions options, CancellationToken cancellationToken)
    {
        var parameters = options.TokenValidationParameters;
        if (parameters.IssuerSigningKey is not null || parameters.IssuerSigningKeys?.Any() == true)
        {
            return false;
        }

        var configuration = options.Configuration;
        if (configuration is null && options.ConfigurationManager is not null)
        {
            try
            {
                configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or OperationCanceledException)
            {
                return true;
            }
        }

        return configuration is null || configuration.SigningKeys.Count == 0;
    }
}
