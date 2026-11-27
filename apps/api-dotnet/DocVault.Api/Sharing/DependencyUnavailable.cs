// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using Microsoft.AspNetCore.Diagnostics;

namespace DocVault.Api.Sharing;

/// <summary>
/// OpenFGA or Keycloak's Admin API could not answer. The request fails with 503.
/// </summary>
/// <remarks>
/// Never a 404 or a 403: those would be decisions, and none was made. Never an empty
/// list either, which reads as "you have no documents". And never "allow". The reason is
/// a fixed word, never a response body or an exception's text.
/// </remarks>
public sealed class DependencyUnavailableException(string dependency, string reason, Exception? inner = null)
    : Exception($"{dependency} unavailable: {reason}", inner)
{
    public string Dependency { get; } = dependency;

    public string Reason { get; } = reason;
}

public sealed class DependencyUnavailableExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<DependencyUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DependencyUnavailableException unavailable)
        {
            return false;
        }

        logger.LogWarning("Refused a request: {Dependency} unavailable ({Reason}).", unavailable.Dependency, unavailable.Reason);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        // No Exception on the context: the response says what failed, in fixed words.
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Authorization unavailable",
                Detail = "Who may see which document could not be decided just now. Try again shortly.",
            },
        });
    }
}
