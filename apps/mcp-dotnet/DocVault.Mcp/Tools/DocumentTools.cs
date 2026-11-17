// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.ComponentModel;
using System.Text.Json;
using DocVault.Mcp.Audit;
using DocVault.Mcp.Auth;
using DocVault.Mcp.Downstream;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DocVault.Mcp.Tools;

/// <summary>
/// The tools an assistant gets. Each needs one scope (see <see cref="ToolScopes"/>)
/// and calls the API as the user. Tool arguments come from a language model and are
/// treated as untrusted: typed, bounded, and never used to choose a tenant or a user.
/// </summary>
[McpServerToolType]
public sealed class DocumentTools(
    IHttpContextAccessor accessor,
    DocVaultApiClient api,
    IAuditLog audit)
{
    private const int MaxTitleLength = 200;

    [McpServerTool(Name = ToolScopes.ListDocuments, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List the documents in the signed-in user's tenant.")]
    public Task<CallToolResult> ListDocuments(CancellationToken cancellationToken) =>
        RunAsync(ToolScopes.ListDocuments, token => api.ListDocumentsAsync(token, cancellationToken), cancellationToken);

    [McpServerTool(Name = ToolScopes.CreateDocument, ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Create a document in the signed-in user's tenant. Needs the editor or admin role in DocVault.")]
    public Task<CallToolResult> CreateDocument(
        [Description("Title of the new document, 1-200 characters.")] string title,
        CancellationToken cancellationToken)
    {
        // Control AND format characters: bidi overrides and zero-width characters
        // can make a title read differently to a person than to a model.
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength || HasInvisible(title))
        {
            // The length, not the text: a refused title is refused for what it may carry.
            audit.Record(AuditEvents.ToolCall, AuditOutcomes.Rejected, accessor.HttpContext,
                ("tool", ToolScopes.CreateDocument), ("reason", "invalid_title"), ("document.title.length", title?.Length ?? 0));
            return Task.FromResult(Error($"A title must be 1-{MaxTitleLength} characters with no control or invisible characters."));
        }

        // A title that passed the check above has no control or invisible characters.
        return RunAsync(ToolScopes.CreateDocument, token => api.CreateDocumentAsync(token, title.Trim(), cancellationToken),
            cancellationToken, ("document.title", title.Trim()));
    }

    [McpServerTool(Name = ToolScopes.DeleteDocument, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Permanently delete a document by id. Needs the admin role in DocVault.")]
    public Task<CallToolResult> DeleteDocument(
        [Description("The document's id (a GUID), as returned by list_documents.")] string id,
        CancellationToken cancellationToken)
    {
        // Parsed here rather than by the SDK, so a bad id gets a sentence the model can act on.
        if (!Guid.TryParse(id, out var documentId))
        {
            audit.Record(AuditEvents.ToolCall, AuditOutcomes.Rejected, accessor.HttpContext,
                ("tool", ToolScopes.DeleteDocument), ("reason", "invalid_id"));
            return Task.FromResult(Error("The id must be a GUID, as returned by list_documents."));
        }

        return RunAsync(ToolScopes.DeleteDocument, token => api.DeleteDocumentAsync(token, documentId, cancellationToken),
            cancellationToken, ("document.id", documentId.ToString()));
    }

    private async Task<CallToolResult> RunAsync(
        string tool, Func<string, Task<ApiResult>> call, CancellationToken cancellationToken,
        params (string Key, object? Value)[] subject)
    {
        var context = accessor.HttpContext ?? throw new McpException("No HTTP request for this call.");
        var user = context.User;

        // Defence in depth. ScopeChallengeMiddleware has already answered a call
        // without the scope with a 403 challenge; this holds if it is ever bypassed.
        var scope = ToolScopes.RequiredFor(tool)!;
        if (!user.HasScope(scope))
        {
            Audit(context, tool, AuditOutcomes.Refused, "missing_scope", subject, ("scope.required", scope));
            throw new McpException($"This tool needs the '{scope}' permission.");
        }

        var token = BearerToken(context) ?? throw new McpException("No bearer token on this request.");

        ApiResult result;
        try
        {
            result = await call(token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Keycloak or the API could not be reached. Whether the API acted is not
            // known here, and the record says so instead of saying nothing.
            Audit(context, tool, AuditOutcomes.Error, ex.GetType().Name, subject);
            throw;
        }

        // One record per tool call: who, through which client, what, and how it ended.
        var outcome = result.Ok ? AuditOutcomes.Ok
            : result.Reason == "not_found" ? AuditOutcomes.NotFound
            : result.Reason is "exchange_refused" or "api_forbidden" or "api_unauthorized" ? AuditOutcomes.Refused
            : AuditOutcomes.Error;
        Audit(context, tool, outcome, result.Reason, subject, ("document.id", result.Ok ? CreatedId(tool, result.Text) : null));

        return result.Ok ? Text(result.Text) : Error(result.Text);
    }

    private void Audit(
        HttpContext context, string tool, string outcome, string? reason,
        (string Key, object? Value)[] subject, params (string Key, object? Value)[] more) =>
        audit.Record(AuditEvents.ToolCall, outcome, context,
            [("tool", tool), ("reason", reason), .. subject, .. more]);

    /// <summary>The id the API gave a new document, so its later deletion can be matched to it.</summary>
    private static string? CreatedId(string tool, string body)
    {
        if (tool != ToolScopes.CreateDocument)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out var parsed)
                    ? parsed.ToString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// By rune, not by char: the Unicode tag characters (U+E0000-E007F), the usual
    /// carrier for hidden text, lie outside the BMP, where each half of the
    /// surrogate pair has the category "Surrogate" and would pass a per-char test.
    /// </summary>
    private static bool HasInvisible(string title)
    {
        foreach (var rune in title.EnumerateRunes())
        {
            // EnumerateRunes yields U+FFFD for a lone surrogate.
            if (rune == System.Text.Rune.ReplacementChar
                || System.Text.Rune.IsControl(rune)
                || System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator
                    or System.Globalization.UnicodeCategory.PrivateUse
                    or System.Globalization.UnicodeCategory.OtherNotAssigned)
            {
                return true;
            }
        }

        return false;
    }

    private static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }

    private static CallToolResult Text(string text) =>
        new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Error(string text) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = text }] };
}
