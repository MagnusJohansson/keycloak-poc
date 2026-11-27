// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DocVault.Api.Sharing;

/// <summary>Finding the people and departments a document can be shared with.</summary>
public interface IPeopleDirectory
{
    /// <summary>
    /// The Keycloak user id of the person with this username, if they are in this
    /// tenant. Null when there is no such person AND when they are in another tenant:
    /// the two answers must look the same, or a share request becomes a way to find out
    /// who works at globex.
    /// </summary>
    Task<Guid?> FindInTenantAsync(string username, string tenant, CancellationToken cancellationToken);

    /// <summary>Whether <c>/tenant/department</c> is a group in the realm.</summary>
    Task<bool> DepartmentExistsAsync(string tenant, string department, CancellationToken cancellationToken);

    /// <summary>
    /// Someone a document is shared with, as they are now: their current username, and
    /// whether they are still in this tenant. Null if they no longer exist.
    /// </summary>
    Task<SharedPerson?> SharedPersonAsync(Guid userId, string tenant, CancellationToken cancellationToken);
}

/// <summary>
/// A person a share names. <see cref="InTenant"/> false: they have left the document's
/// tenant, and the share grants them nothing (the model intersects every permission with
/// the tenant) until they come back, when it would grant it again.
/// </summary>
public sealed record SharedPerson(string Username, bool InTenant);

/// <summary>
/// Keycloak's Admin API, as the <c>docvault-api-directory</c> client (20-realm/directory.tf),
/// which holds view-users and nothing else.
/// </summary>
/// <remarks>
/// Asked only when a document is shared or its shares are listed. Reading documents never
/// calls it, so Keycloak is not on the path of every request.
///
/// A lookup by username finds a directory (LDAP) person who has never signed in, and
/// Keycloak imports them as a side effect. A brokered SAML person exists only after their
/// first sign-in, so before it they cannot be shared with.
/// </remarks>
public sealed class KeycloakPeopleDirectory(
    HttpClient http,
    IConfiguration configuration,
    IOptions<SharingSettings> options,
    DirectoryToken token,
    TimeProvider time,
    ILogger<KeycloakPeopleDirectory> logger) : IPeopleDirectory
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string _issuer = configuration["Keycloak:Authority"]!.TrimEnd('/');
    private readonly SharingSettings _settings = options.Value;

    /// <summary><c>http://host/realms/docvault</c> → <c>http://host/admin/realms/docvault/</c>.</summary>
    private string AdminBase
    {
        get
        {
            var at = _issuer.LastIndexOf("/realms/", StringComparison.Ordinal);
            return $"{_issuer[..at]}/admin{_issuer[at..]}/";
        }
    }

    public async Task<Guid?> FindInTenantAsync(string username, string tenant, CancellationToken cancellationToken)
    {
        using var users = await GetJsonAsync(
            $"users?username={Uri.EscapeDataString(username)}&exact=true&briefRepresentation=true", cancellationToken);

        // exact=true matches the username exactly, but say so here too rather than trust it.
        var match = users?.RootElement.EnumerateArray()
            .FirstOrDefault(u => string.Equals(u.GetProperty("username").GetString(), username, StringComparison.Ordinal));

        if (match is not { ValueKind: JsonValueKind.Object } user
            || !Guid.TryParse(user.GetProperty("id").GetString(), out var id))
        {
            return null;
        }

        return await InTenantAsync(id, tenant, cancellationToken) ? id : null;
    }

    public async Task<bool> DepartmentExistsAsync(string tenant, string department, CancellationToken cancellationToken)
    {
        using var group = await GetJsonAsync(
            $"group-by-path/{Uri.EscapeDataString(tenant)}/{Uri.EscapeDataString(department)}", cancellationToken);
        return group is not null;
    }

    public async Task<SharedPerson?> SharedPersonAsync(Guid userId, string tenant, CancellationToken cancellationToken)
    {
        using var user = await GetJsonAsync($"users/{userId:D}", cancellationToken);
        if (user?.RootElement.GetProperty("username").GetString() is not { } username)
        {
            return null;
        }

        return new SharedPerson(username, await InTenantAsync(userId, tenant, cancellationToken));
    }

    /// <summary>Whether the person is in <c>/tenant</c> or a group under it: the same test the API applies to a token.</summary>
    private async Task<bool> InTenantAsync(Guid userId, string tenant, CancellationToken cancellationToken)
    {
        using var groups = await GetJsonAsync($"users/{userId:D}/groups?briefRepresentation=true", cancellationToken);
        return groups?.RootElement.EnumerateArray()
            .Select(g => g.GetProperty("path").GetString() ?? string.Empty)
            .Any(path => path == $"/{tenant}" || path.StartsWith($"/{tenant}/", StringComparison.Ordinal)) == true;
    }

    /// <summary>The response, or null for 404. Anything else that is not a success throws.</summary>
    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AdminBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancellationToken));

        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // The path holds a username; log the status only.
            logger.LogError("Keycloak's Admin API refused a lookup: HTTP {Status}.", (int)response.StatusCode);
            throw new DependencyUnavailableException("keycloak", $"http_{(int)response.StatusCode}");
        }

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    /// <summary>A client_credentials token for the directory client, reused until shortly before it expires.</summary>
    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await token.Lock.WaitAsync(cancellationToken);
        try
        {
            if (token.Value is not null && time.GetUtcNow() < token.Expires)
            {
                return token.Value;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_issuer}/protocol/openid-connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _settings.DirectoryClientId,
                    ["client_secret"] = _settings.DirectoryClientSecret!,
                }),
            };

            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Keycloak refused the directory client's token request: HTTP {Status}.", (int)response.StatusCode);
                throw new DependencyUnavailableException("keycloak", "token_refused");
            }

            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            token.Value = body.RootElement.GetProperty("access_token").GetString();
            var lifetime = body.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 60;
            token.Expires = time.GetUtcNow().AddSeconds(Math.Max(0, lifetime - 30));
            return token.Value!;
        }
        finally
        {
            token.Lock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("keycloak", ex is TaskCanceledException ? "timeout" : "unreachable", ex);
        }
    }
}

/// <summary>
/// The directory client's token, shared by every <see cref="KeycloakPeopleDirectory"/>. A
/// singleton because the directory itself is a typed HttpClient, which is made anew for
/// each request: a token kept there would be fetched again for every share.
/// </summary>
public sealed class DirectoryToken
{
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public string? Value { get; set; }

    public DateTimeOffset Expires { get; set; }
}
