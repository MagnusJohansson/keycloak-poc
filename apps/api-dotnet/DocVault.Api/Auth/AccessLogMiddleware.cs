// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Diagnostics;

namespace DocVault.Api.Auth;

/// <summary>
/// One line per authenticated request: who, through which client, and how it ended.
///
/// The client (<c>azp</c>) is how the API can tell an assistant from the user's own
/// hands. A request the user made in the web app arrives with <c>docvault-web</c>;
/// one an assistant made for them arrives with the MCP server's client, because the
/// MCP server exchanged the token. Same user, same roles, a different client.
///
/// Sits between authentication and authorization on purpose: after authorization
/// it would never see the requests authorization refuses, and those are the ones
/// worth a line.
/// </summary>
public sealed class AccessLogMiddleware(RequestDelegate next, ILoggerFactory loggers)
{
    public const string Category = "DocVault.Api.Access";

    private readonly ILogger _logger = loggers.CreateLogger(Category);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch
        {
            // The status is set later, by the exception handler.
            Write(context, "error");
            throw;
        }

        Write(context, context.Response.StatusCode.ToString());
    }

    private void Write(HttpContext context, string status)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            // A refused token is logged where it is refused (DocVault.Auth).
            return;
        }

        // The route's pattern, not the request's path: the path is the caller's text
        // and, decoded, may contain a line break.
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";

        _logger.LogInformation(
            "access method={Method} route={Route} status={Status} user={User} sub={Subject} client={Client} trace={TraceId}",
            MethodName(context.Request.Method), route, status,
            user.Identity.Name, user.FindFirst("sub")?.Value, user.FindFirst("azp")?.Value,
            Activity.Current?.TraceId.ToString());
    }

    /// <summary>
    /// The method as one of a fixed set of names. Like the path, the method is the
    /// caller's text, and the log gets none of it.
    /// </summary>
    private static string MethodName(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "GET",
        "POST" => "POST",
        "PUT" => "PUT",
        "DELETE" => "DELETE",
        "PATCH" => "PATCH",
        "HEAD" => "HEAD",
        "OPTIONS" => "OPTIONS",
        _ => "OTHER",
    };
}
