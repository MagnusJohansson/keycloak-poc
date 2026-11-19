// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace DocVault.Reports.Saml;

/// <summary>
/// Ties a SAML response to the sign-in that asked for it.
///
/// A signed response proves who signed it. It does not prove that THIS browser
/// asked for it, or that it has not been used before. Without those two checks
/// a response taken from one person's sign-in can be posted from another
/// browser and is accepted: the signature is still good.
///
/// So the id of each request goes into a cookie in the browser that started it,
/// encrypted so that the browser cannot write its own, and a response is
/// accepted only if it answers that id. Each assertion is accepted once, and
/// each request is answered once.
/// </summary>
public sealed class PendingSignIn(IDataProtectionProvider protection, IMemoryCache used, TimeProvider clock)
{
    public const string CookieName = "reports.signin";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly IDataProtector _protector = protection.CreateProtector("DocVault.Reports.PendingSignIn.v1");

    private sealed record Ticket(string RequestId, string ReturnUrl, DateTimeOffset Expires);

    public void Remember(HttpResponse response, string requestId, string returnUrl)
    {
        var ticket = new Ticket(requestId, returnUrl, clock.GetUtcNow() + Lifetime);
        response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(ticket)), new CookieOptions
        {
            HttpOnly = true,
            // The response comes back as a POST from the identity provider's
            // site, and a browser sends only SameSite=None cookies with that.
            // None requires Secure; browsers accept Secure on localhost.
            SameSite = SameSiteMode.None,
            Secure = true,
            Path = "/saml",
            MaxAge = Lifetime,
            IsEssential = true,
        });
    }

    /// <summary>
    /// The pending sign-in of this browser. Null if there is none. Reading does
    /// not use it up: what stops a second use is <see cref="FirstUse"/>.
    /// </summary>
    public (string RequestId, string ReturnUrl)? Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var ticket = JsonSerializer.Deserialize<Ticket>(_protector.Unprotect(value));
            return ticket is null || ticket.Expires < clock.GetUtcNow() ? null : (ticket.RequestId, ticket.ReturnUrl);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null; // not ours, or changed
        }
    }

    public void Clear(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/saml", Secure = true, SameSite = SameSiteMode.None });

    /// <summary>True the first time a key is seen, false after that.</summary>
    public bool FirstUse(string key, DateTimeOffset notOnOrAfter)
    {
        lock (used)
        {
            if (used.TryGetValue(key, out _))
            {
                return false;
            }

            // Kept until the assertion would have expired anyway, and a little
            // longer for clocks that disagree.
            var until = notOnOrAfter > clock.GetUtcNow() ? notOnOrAfter : clock.GetUtcNow();
            used.Set(key, true, until + TimeSpan.FromMinutes(5));
            return true;
        }
    }
}
