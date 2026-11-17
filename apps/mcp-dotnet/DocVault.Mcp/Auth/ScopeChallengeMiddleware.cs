// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Text.Json;
using DocVault.Mcp.Audit;

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
public sealed class ScopeChallengeMiddleware(RequestDelegate next, McpSettings settings, IAuditLog audit)
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
            TooLarge(context);
            return;
        }

        context.Request.EnableBuffering(MaxInspectedBytes);
        var missing = await MissingScopesAsync(context);
        context.Request.Body.Position = 0;

        if (missing is { TooLarge: true })
        {
            // A chunked body with no Content-Length that turned out to be large.
            TooLarge(context);
            return;
        }

        if (missing is null)
        {
            // Fail closed. A body this cannot read (a byte-order mark, UTF-16) might
            // still be one the SDK can, and it would then reach a tool unchecked.
            audit.Record(AuditEvents.RequestRefused, AuditOutcomes.Rejected, context, ("reason", "unreadable_body"));
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (missing.Scopes.Count > 0)
        {
            var scopes = string.Join(' ', missing.Scopes);
            // The tool names are the server's own (ToolScopes), matched from the
            // request; nothing else from the body is recorded.
            audit.Record(AuditEvents.ScopeChallenge, AuditOutcomes.Refused, context,
                ("reason", "missing_scope"),
                ("tool", missing.Tools.Count > 0 ? string.Join(' ', missing.Tools) : null),
                ("scope.required", scopes));

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers.WWWAuthenticate =
                $"Bearer error=\"insufficient_scope\", scope=\"{scopes}\", " +
                $"resource_metadata=\"{settings.ResourceMetadataUrl}\", " +
                "error_description=\"The assistant has not been granted this permission.\"";
            return;
        }

        await next(context);
    }

    /// <summary>Not inspected, so not allowed through to a tool either.</summary>
    private void TooLarge(HttpContext context)
    {
        audit.Record(AuditEvents.RequestRefused, AuditOutcomes.Rejected, context, ("reason", "too_large"));
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
    }

    /// <summary>The scopes the token lacks, and the tools that needed them.</summary>
    private sealed record Missing(SortedSet<string> Scopes, SortedSet<string> Tools, bool TooLarge = false);

    /// <summary>
    /// The scopes the request's tool calls need and the token lacks: empty when it
    /// lacks none, null when the body is not UTF-8 JSON this can read.
    /// </summary>
    private static async Task<Missing?> MissingScopesAsync(HttpContext context)
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
                    return new Missing([], [], TooLarge: true);
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
            var missing = new Missing(new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));

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
                var tool = name.GetString()!;
                var required = ToolScopes.RequiredFor(tool);
                if (required is not null && !context.User.HasScope(required))
                {
                    missing.Scopes.Add(required);
                    missing.Tools.Add(tool);
                }
            }

            return missing;
        }
    }
}
