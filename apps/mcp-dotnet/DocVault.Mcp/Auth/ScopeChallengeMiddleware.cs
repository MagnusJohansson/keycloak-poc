// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Text.Json;

namespace DocVault.Mcp.Auth;

/// <summary>
/// Answers a tool call the token has no scope for with the challenge MCP specifies:
/// HTTP 403 and <c>WWW-Authenticate: Bearer error="insufficient_scope", scope="…"</c>.
/// A client can act on that (ask the user for the missing scope and retry); it
/// cannot act on a JSON-RPC error saying "forbidden".
///
/// The MCP C# SDK (2.4) does not send this challenge itself, and the tool's name is
/// in the JSON-RPC body, so this reads the body. Runs after authentication and
/// authorization: only requests with a valid token for this server get here.
/// </summary>
public sealed class ScopeChallengeMiddleware(RequestDelegate next, McpSettings settings, ILogger<ScopeChallengeMiddleware> logger)
{
    /// <summary>Larger bodies are left to the SDK; no tool here takes large arguments.</summary>
    private const int MaxInspectedBytes = 64 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        // Routing also matches "/mcp/", so compare without a trailing slash: a path
        // the endpoint serves and this skipped would get no challenge.
        var path = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
        if (!HttpMethods.IsPost(context.Request.Method)
            || !string.Equals(path, settings.McpPath, StringComparison.OrdinalIgnoreCase)
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength is > MaxInspectedBytes)
        {
            // Not inspected, so not allowed through to a tool either.
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        context.Request.EnableBuffering(MaxInspectedBytes);
        var missing = await MissingScopesAsync(context);
        context.Request.Body.Position = 0;

        if (missing is null)
        {
            // Fail closed. A body this cannot read (a byte-order mark, UTF-16) might
            // still be one the SDK can, and it would then reach a tool unchecked.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (missing.Count > 0)
        {
            var scopes = string.Join(' ', missing);
            logger.LogWarning(
                "Refused {Method}: token of {User} via {Client} lacks scope {Scopes}.",
                "tools/call", context.User.Identity?.Name, context.User.FindFirst("azp")?.Value, scopes);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers.WWWAuthenticate =
                $"Bearer error=\"insufficient_scope\", scope=\"{scopes}\", " +
                $"resource_metadata=\"{settings.ResourceMetadataUrl}\", " +
                "error_description=\"The assistant has not been granted this permission.\"";
            return;
        }

        await next(context);
    }

    /// <summary>
    /// The scopes the request's tool calls need and the token lacks: empty when it
    /// lacks none, null when the body is not UTF-8 JSON this can read.
    /// </summary>
    private static async Task<SortedSet<string>?> MissingScopesAsync(HttpContext context)
    {
        JsonDocument document;
        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (limited.Length + read > MaxInspectedBytes)
                {
                    // Chunked body with no Content-Length that turned out to be large.
                    return new SortedSet<string>(ToolScopes.All.Values);
                }

                limited.Write(buffer, 0, read);
            }

            document = JsonDocument.Parse(limited.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var missing = new SortedSet<string>(StringComparer.Ordinal);

            // A JSON-RPC batch is an array; every call in it is checked.
            var messages = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];

            foreach (var message in messages)
            {
                if (message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("method", out var method)
                    || method.ValueKind != JsonValueKind.String
                    || method.GetString() != "tools/call"
                    || !message.TryGetProperty("params", out var parameters)
                    || parameters.ValueKind != JsonValueKind.Object
                    || !parameters.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                // An unknown tool needs no scope here; the SDK answers "unknown tool".
                var required = ToolScopes.RequiredFor(name.GetString()!);
                if (required is not null && !context.User.HasScope(required))
                {
                    missing.Add(required);
                }
            }

            return missing;
        }
    }
}
