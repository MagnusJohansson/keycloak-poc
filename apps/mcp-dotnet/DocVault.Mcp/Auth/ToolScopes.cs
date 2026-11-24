// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;

namespace DocVault.Mcp.Auth;

/// <summary>
/// Which OAuth scope each tool needs. The one place this is written down: the
/// HTTP-level scope challenge and the tools themselves both read it.
///
/// Scopes limit the assistant. They are not the user's permissions: the API still
/// applies the user's roles and tenant to the exchanged token, so a call succeeds
/// only where the user consented AND the user is allowed.
/// </summary>
public static class ToolScopes
{
    public const string Read = "docvault:read";
    public const string Write = "docvault:write";
    public const string Delete = "docvault:delete";

    /// <summary>
    /// Sharing is its own scope, and not one a client asks for at first: a share is how a
    /// document leaves the people who can see it, which makes it the tool a prompt
    /// injection would most want. The first time a client asks for it, the consent screen
    /// shows it on its own line. After that Keycloak remembers the grant for the user and
    /// this client, which every assistant shares, and asks again only if the client sends
    /// prompt=consent or the consent is revoked (by the user on the account console's
    /// Applications page, or by an administrator).
    /// </summary>
    public const string Share = "docvault:share";

    public const string ListDocuments = "list_documents";
    public const string CreateDocument = "create_document";
    public const string DeleteDocument = "delete_document";
    public const string ShareDocument = "share_document";
    public const string UnshareDocument = "unshare_document";

    private static readonly Dictionary<string, string> Required = new(StringComparer.Ordinal)
    {
        [ListDocuments] = Read,
        [CreateDocument] = Write,
        [DeleteDocument] = Delete,
        [ShareDocument] = Share,
        [UnshareDocument] = Share,
    };

    /// <summary>
    /// What a client is told to ask for first (RFC 9728 scopes_supported). Deliberately
    /// only reading: MCP treats this list as the minimum for basic use, and the other
    /// scopes are requested when a tool that needs them is called.
    /// </summary>
    public static readonly string[] Initial = [Read];

    public static IReadOnlyDictionary<string, string> All => Required;

    public static string? RequiredFor(string tool) => Required.GetValueOrDefault(tool);

    /// <summary>The token's scopes. No hierarchy: docvault:delete does not imply docvault:write.</summary>
    public static bool HasScope(this ClaimsPrincipal user, string scope) =>
        (user.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(scope, StringComparer.Ordinal);
}
