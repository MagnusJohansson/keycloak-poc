// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

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
}
