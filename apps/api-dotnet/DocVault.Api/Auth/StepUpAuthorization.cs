// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace DocVault.Api.Auth;

/// <summary>
/// Requires that the caller authenticated at a given ACR (Authentication Context Class
/// Reference) — in this lab, <c>silver</c>, meaning password + OTP.
/// </summary>
/// <remarks>
/// The <c>acr</c> claim is placed in the token by Keycloak according to the realm's
/// <c>acr.loa.map</c> attribute. See <c>infra/terraform/20-realm/realm.tf</c>: without that
/// map, <c>acr_values</c> is silently ignored and this check can never pass.
/// </remarks>
public sealed class StepUpAcrRequirement(string requiredAcr) : IAuthorizationRequirement
{
    public string RequiredAcr { get; } = requiredAcr;
}

public sealed class StepUpAcrHandler(ILogger<StepUpAcrHandler> logger)
    : AuthorizationHandler<StepUpAcrRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StepUpAcrRequirement requirement)
    {
        var acr = context.User.FindFirst("acr")?.Value;

        if (string.Equals(acr, requirement.RequiredAcr, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Deliberately NOT calling context.Fail(). Leaving the requirement merely unmet lets
        // the result handler below distinguish "needs to step up" from "will never be allowed",
        // so the client gets an actionable challenge instead of a dead end.
        logger.LogInformation(
            "Step-up required: token acr={ActualAcr}, needed {RequiredAcr}.", acr ?? "(none)", requirement.RequiredAcr);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Turns an unmet <see cref="StepUpAcrRequirement"/> into a spec-compliant challenge telling
/// the client exactly how to fix it, rather than an opaque 403.
/// </summary>
/// <remarks>
/// <para>
/// Per RFC 9470 (OAuth 2.0 Step Up Authentication Challenge Protocol) the resource server
/// responds with <c>insufficient_user_authentication</c> and the <c>acr_values</c> it needs.
/// The SPA reads that header and re-runs the authorization request with
/// <c>acr_values=silver</c>. Without it the browser has no way to know that re-authenticating
/// would help, and the user just sees "forbidden".
/// </para>
/// </remarks>
public sealed class StepUpAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        var stepUp = authorizeResult.AuthorizationFailure?.FailedRequirements
            .OfType<StepUpAcrRequirement>()
            .FirstOrDefault();

        // Only challenge a caller who is already authenticated. An anonymous caller needs a
        // plain 401 first — telling them to "step up" before they have logged in is nonsense.
        if (stepUp is not null && context.User.Identity?.IsAuthenticated == true)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers.WWWAuthenticate =
                $"""Bearer error="insufficient_user_authentication", error_description="A higher authentication level is required", acr_values="{stepUp.RequiredAcr}" """.Trim();

            await context.Response.WriteAsJsonAsync(new
            {
                error = "insufficient_user_authentication",
                required_acr = stepUp.RequiredAcr,
                detail = "Re-authenticate with acr_values=" + stepUp.RequiredAcr,
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
