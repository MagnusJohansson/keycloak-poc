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
public sealed partial class DocumentTools(
    IHttpContextAccessor accessor,
    DocVaultApiClient api,
    IAuditLog audit)
{
    private const int MaxTitleLength = 200;
    private const int MaxNameLength = 255;

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
            audit.Record(AuditEvents.ToolCall, AuditOutcomes.Rejected, AuditCaller.From(accessor.HttpContext),
                ("tool", ToolScopes.CreateDocument), ("reason", "invalid_title"), ("document.title.length", title?.Length ?? 0));
            return Task.FromResult(Error($"A title must be 1-{MaxTitleLength} characters with no control or invisible characters."));
        }

        // A title that passed the check above has no control or invisible characters.
        return RunAsync(ToolScopes.CreateDocument, token => api.CreateDocumentAsync(token, title.Trim(), cancellationToken),
            cancellationToken, ("document.title", title.Trim()));
    }

    [McpServerTool(Name = ToolScopes.DeleteDocument, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Permanently delete a document by id. Needs the admin role in DocVault, or owning the document.")]
    public Task<CallToolResult> DeleteDocument(
        [Description("The document's id (a GUID), as returned by list_documents.")] string id,
        CancellationToken cancellationToken)
    {
        // Parsed here rather than by the SDK, so a bad id gets a sentence the model can act on.
        if (!Guid.TryParse(id, out var documentId))
        {
            audit.Record(AuditEvents.ToolCall, AuditOutcomes.Rejected, AuditCaller.From(accessor.HttpContext),
                ("tool", ToolScopes.DeleteDocument), ("reason", "invalid_id"));
            return Task.FromResult(Error("The id must be a GUID, as returned by list_documents."));
        }

        return RunAsync(ToolScopes.DeleteDocument, token => api.DeleteDocumentAsync(token, documentId, cancellationToken),
            cancellationToken, ("document.id", documentId.ToString()));
    }

    /// <remarks>
    /// The tool a prompt injection would most want: a share moves a document past the
    /// people who could see it. Three things stand in the way, none of them the model.
    /// It needs <see cref="ToolScopes.Share"/>, which the user consents to separately.
    /// The API lets only the owner share, and only with someone in the same tenant. And
    /// the record below names who it went to. Inside the tenant, nothing here can tell
    /// a share the user meant from one a document told the assistant to make.
    /// </remarks>
    [McpServerTool(Name = ToolScopes.ShareDocument, ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Share one of the signed-in user's own documents with a person or a department of their organisation, so they can read it. Give exactly one of username or department.")]
    public Task<CallToolResult> ShareDocument(
        [Description("The document's id (a GUID), as returned by list_documents.")] string id,
        [Description("The username of one person to share with.")] string? username = null,
        [Description("The name of one department to share with, e.g. legal.")] string? department = null,
        CancellationToken cancellationToken = default)
    {
        if (ShareArguments(ToolScopes.ShareDocument, id, username, department) is { } refusal)
        {
            return Task.FromResult(refusal);
        }

        var documentId = Guid.Parse(id);
        return RunAsync(ToolScopes.ShareDocument,
            token => api.ShareAsync(token, documentId, username?.Trim(), department?.Trim(), cancellationToken),
            cancellationToken, ("document.id", documentId.ToString()), ("share.with", ShareWith(username, department)));
    }

    [McpServerTool(Name = ToolScopes.UnshareDocument, ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Stop sharing one of the signed-in user's own documents with a person or a department. Give exactly one of username or department.")]
    public Task<CallToolResult> UnshareDocument(
        [Description("The document's id (a GUID), as returned by list_documents.")] string id,
        [Description("The username of the person to stop sharing with.")] string? username = null,
        [Description("The name of the department to stop sharing with.")] string? department = null,
        CancellationToken cancellationToken = default)
    {
        if (ShareArguments(ToolScopes.UnshareDocument, id, username, department) is { } refusal)
        {
            return Task.FromResult(refusal);
        }

        var documentId = Guid.Parse(id);
        return RunAsync(ToolScopes.UnshareDocument, async token =>
        {
            if (department is not null)
            {
                return await api.UnshareDepartmentAsync(token, documentId, department.Trim(), cancellationToken);
            }

            // The API removes a person's share by user id, which survives a rename. The
            // assistant knows a username, so look the id up in the document's shares.
            var shares = await api.ListSharesAsync(token, documentId, cancellationToken);
            if (!shares.Ok)
            {
                return shares;
            }

            var userId = SharedUserId(shares.Text, username!.Trim());
            return userId is null
                ? new ApiResult(false, "That document is not shared with that person.", "not_found")
                : await api.UnsharePersonAsync(token, documentId, userId.Value, cancellationToken);
        }, cancellationToken, ("document.id", documentId.ToString()), ("share.with", ShareWith(username, department)));
    }

    /// <summary>An error result for bad share arguments, recorded; null when they are fine.</summary>
    private CallToolResult? ShareArguments(string tool, string id, string? username, string? department)
    {
        var reason =
            !Guid.TryParse(id, out _) ? "invalid_id"
            : (username is null) == (department is null) ? "invalid_target"
            : username is not null && (string.IsNullOrWhiteSpace(username) || username.Trim().Length > MaxNameLength || HasInvisible(username)) ? "invalid_target"
            : department is not null && !DepartmentName().IsMatch(department.Trim()) ? "invalid_target"
            : null;

        if (reason is null)
        {
            return null;
        }

        // Not the name itself: a refused argument is refused for what it may carry.
        audit.Record(AuditEvents.ToolCall, AuditOutcomes.Rejected, AuditCaller.From(accessor.HttpContext),
            ("tool", tool), ("reason", reason));
        return Error(reason == "invalid_id"
            ? "The id must be a GUID, as returned by list_documents."
            : "Give exactly one of username or department: a username of up to 255 visible characters, or a department name of letters, digits, '.', '_' and '-'.");
    }

    private static string ShareWith(string? username, string? department) =>
        username is not null ? $"user:{username.Trim().ToLowerInvariant()}" : $"department:{department!.Trim()}";

    private static Guid? SharedUserId(string sharesJson, string username)
    {
        try
        {
            using var json = JsonDocument.Parse(sharesJson);
            foreach (var share in json.RootElement.GetProperty("shares").EnumerateArray())
            {
                if (share.GetProperty("type").GetString() == "user"
                    && string.Equals(share.GetProperty("username").GetString(), username, StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParse(share.GetProperty("id").GetString(), out var userId))
                {
                    return userId;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        return null;
    }

    // As the API's FgaNames.IsSafeName: a letter or digit first ("." and ".." are path
    // segments), and \z rather than $, which also matches before a final newline.
    [System.Text.RegularExpressions.GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial System.Text.RegularExpressions.Regex DepartmentName();

    private async Task<CallToolResult> RunAsync(
        string tool, Func<string, Task<ApiResult>> call, CancellationToken cancellationToken,
        params (string Key, object? Value)[] subject)
    {
        var context = accessor.HttpContext ?? throw new McpException("No HTTP request for this call.");
        var user = context.User;
        // Taken now: the record is written after the API call, and by then the
        // caller may have hung up.
        var caller = AuditCaller.From(context);

        // Defence in depth. ScopeChallengeMiddleware has already answered a call
        // without the scope with a 403 challenge; this holds if it is ever bypassed.
        var scope = ToolScopes.RequiredFor(tool)!;
        if (!user.HasScope(scope))
        {
            Audit(caller, tool, AuditOutcomes.Refused, "missing_scope", subject, ("scope.required", scope));
            throw new McpException($"This tool needs the '{scope}' permission.");
        }

        var token = BearerToken(context) ?? throw new McpException("No bearer token on this request.");

        ApiResult result;
        try
        {
            result = await call(token);
        }
        catch (Exception ex)
        {
            // Keycloak or the API could not be reached, or the caller hung up while
            // the API was working. Whether the API acted is not known here, and the
            // record says so instead of saying nothing. No filter on cancellation:
            // a caller can hang up on purpose, and the API may still have acted.
            var reason = cancellationToken.IsCancellationRequested ? "cancelled" : ex.GetType().Name;
            Audit(caller, tool, AuditOutcomes.Error, reason, subject);
            throw;
        }

        // One record per tool call: who, through which client, what, and how it ended.
        var outcome = result.Ok ? AuditOutcomes.Ok
            : result.Reason == "not_found" ? AuditOutcomes.NotFound
            : result.Reason is "exchange_refused" or "api_forbidden" ? AuditOutcomes.Refused
            // Everything else is a fault, not a decision: Keycloak failing, or the
            // API not accepting the token this server obtained for it.
            : AuditOutcomes.Error;
        Audit(caller, tool, outcome, result.Reason, subject, ("document.id", result.Ok ? CreatedId(tool, result.Text) : null));

        return result.Ok ? Text(result.Text) : Error(result.Text);
    }

    private void Audit(
        AuditCaller? caller, string tool, string outcome, string? reason,
        (string Key, object? Value)[] subject, params (string Key, object? Value)[] more) =>
        audit.Record(AuditEvents.ToolCall, outcome, caller,
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
