// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using Microsoft.AspNetCore.Diagnostics;

namespace DocVault.Api.Auth;

/// <summary>
/// Turns <see cref="MultipleTenantsException"/> into a 403 problem response, wherever it is thrown.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TenantClaimExtensions.GetTenantContext"/> throws rather than guess which tenant a
/// two-tenant caller means. Unhandled, that surfaced as a 500: a server fault the client can
/// only retry, for what is really "this token is not scoped to one tenant". It is 403 for the
/// same reason "no tenant" is: the caller authenticated fine, and signing in again would not
/// change their memberships.
/// </para>
/// <para>
/// Handled centrally rather than at each call site, so a new endpoint cannot forget it. The
/// detail deliberately omits the tenant names — the caller knows their own groups, but a
/// response body is the wrong place to enumerate tenants.
/// </para>
/// </remarks>
public sealed class MultipleTenantsExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not MultipleTenantsException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Ambiguous tenant",
                Detail = "Your account belongs to more than one tenant; a tenant-scoped token is required.",
            },
        });
    }
}
