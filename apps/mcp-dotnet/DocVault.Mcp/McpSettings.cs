// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

namespace DocVault.Mcp;

/// <summary>
/// The server's configuration, read and checked once at startup. Every check here
/// fails the start instead of running with a security setting silently missing.
/// </summary>
public sealed record McpSettings(
    string Authority,
    bool RequireHttpsMetadata,
    Uri ResourceUrl,
    string ClientSecret,
    Uri ApiBaseUrl,
    string ApiAudience,
    string? AuditFile = null)
{
    /// <summary>The path MCP is served at, taken from the resource URL ("/mcp").</summary>
    public string McpPath => ResourceUrl.AbsolutePath;

    /// <summary>RFC 9728: the metadata path is the well-known prefix plus the resource's path.</summary>
    public string ResourceMetadataPath => $"/.well-known/oauth-protected-resource{ResourceUrl.AbsolutePath}";

    public string ResourceMetadataUrl => $"{ResourceUrl.GetLeftPart(UriPartial.Authority)}{ResourceMetadataPath}";

    /// <summary>The resource exactly as configured: what `aud` must equal.</summary>
    public string Resource => ResourceUrl.OriginalString;

    public string TokenEndpoint => $"{Authority}/protocol/openid-connect/token";

    public static McpSettings Load(IConfiguration configuration)
    {
        var authority = Required(configuration, "Keycloak:Authority").TrimEnd('/');
        var resource = Required(configuration, "Mcp:ResourceUrl");
        var secret = Required(configuration, "Mcp:ClientSecret");
        var apiBase = Required(configuration, "DocVault:ApiBaseUrl");
        var apiAudience = Required(configuration, "DocVault:ApiAudience");

        // Explicit configuration, defaulting to safe. Not derived from the environment
        // name, which is easy to lose (--no-launch-profile silently means Production).
        var requireHttps = configuration.GetValue<bool?>("Keycloak:RequireHttpsMetadata") ?? true;
        if (!requireHttps && !IsLoopback(authority))
        {
            throw new InvalidOperationException(
                $"Keycloak:RequireHttpsMetadata is false but the authority '{authority}' is not loopback. " +
                "Fetching signing keys over plain HTTP from a remote host allows an attacker to substitute " +
                "their own keys and mint valid tokens. Use HTTPS.");
        }

        // Uri.TryCreate accepts "localhost:5002/mcp" as absolute, with "localhost" as the scheme.
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUrl)
            || (resourceUrl.Scheme != Uri.UriSchemeHttp && resourceUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Mcp:ResourceUrl '{resource}' must be an absolute http(s) URL.");
        }

        // RFC 8707: no fragment. And the audience check is an exact string match, so a
        // trailing slash or query here would refuse every token Keycloak issues.
        if (resourceUrl.Fragment.Length > 0 || resourceUrl.Query.Length > 0 || resource.EndsWith('/'))
        {
            throw new InvalidOperationException(
                $"Mcp:ResourceUrl '{resource}' must have no query, fragment or trailing slash: it is compared " +
                "exactly with the token's audience and with the Keycloak client_id.");
        }

        // Clients send their bearer tokens to this URL.
        if (resourceUrl.Scheme == Uri.UriSchemeHttp && !resourceUrl.IsLoopback)
        {
            throw new InvalidOperationException(
                $"Mcp:ResourceUrl '{resource}' is plain HTTP on a non-loopback host; clients would send tokens in clear text.");
        }

        if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var apiBaseUrl)
            || (apiBaseUrl.Scheme != Uri.UriSchemeHttp && apiBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"DocVault:ApiBaseUrl '{apiBase}' must be an absolute http(s) URL.");
        }

        // Requests go to "/documents" on this host; a path here would be dropped silently.
        if (apiBaseUrl.AbsolutePath != "/" || apiBaseUrl.Query.Length > 0)
        {
            throw new InvalidOperationException($"DocVault:ApiBaseUrl '{apiBase}' must be an origin, with no path or query.");
        }

        // The exchanged token (a user's API token) travels to this URL.
        if (apiBaseUrl.Scheme == Uri.UriSchemeHttp && !apiBaseUrl.IsLoopback)
        {
            throw new InvalidOperationException(
                $"DocVault:ApiBaseUrl '{apiBase}' is plain HTTP to a non-loopback host; user tokens would cross the network in clear text.");
        }

        // Optional: the audit trail as a JSON Lines file, as well as in the log.
        var auditFile = configuration["Audit:File"];
        if (string.IsNullOrWhiteSpace(auditFile))
        {
            auditFile = null;
        }
        else if (!Path.IsPathFullyQualified(auditFile))
        {
            // A relative path depends on where the server was started from; the
            // trail would land somewhere nobody looks.
            throw new InvalidOperationException($"Audit:File '{auditFile}' must be an absolute path.");
        }

        return new McpSettings(authority, requireHttps, resourceUrl, secret, apiBaseUrl, apiAudience, auditFile);
    }

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} must be configured.")
            : value;
    }

    private static bool IsLoopback(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || string.Equals(uri.Host, "keycloak", StringComparison.OrdinalIgnoreCase));
}
