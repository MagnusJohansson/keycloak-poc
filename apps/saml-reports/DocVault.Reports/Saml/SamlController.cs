// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Claims;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocVault.Reports.Saml;

/// <summary>
/// The SAML service provider: sends a signed sign-in request to Keycloak, and
/// takes the signed response back.
///
/// The library checks the response's signature, audience and validity period.
/// What it leaves to the application is checked here: that the response was
/// meant for this address, that it answers a request this browser made, and
/// that it has not been used before.
/// </summary>
[AllowAnonymous]
[Route("saml")]
public sealed class SamlController(
    Saml2Configuration saml, ReportsSettings settings, PendingSignIn pending, ILogger<SamlController> logger) : Controller
{
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        var request = new Saml2AuthnRequest(saml)
        {
            AssertionConsumerServiceUrl = settings.AssertionConsumerUrl,
        };
        pending.Remember(Response, request.IdAsString, LocalPath(returnUrl));

        // By POST, so that the signature travels inside the request.
        return new Saml2PostBinding().Bind(request).ToActionResult();
    }

    [HttpPost("acs")]
    public async Task<IActionResult> AssertionConsumerService()
    {
        var started = pending.Take(HttpContext);

        var response = new Saml2AuthnResponse(saml);
        try
        {
            var http = await Request.ToGenericHttpRequestAsync(validate: true);
            http.Binding.ReadSamlResponse(http, response);
            if (response.Status != Saml2StatusCodes.Success)
            {
                return Refuse("status", $"The identity provider answered {response.Status}.");
            }

            // Signature, audience and validity period.
            http.Binding.Unbind(http, response);
        }
        catch (Exception exception)
        {
            // The exception's text can quote the response; its type is enough.
            return Refuse("invalid", exception.GetType().Name);
        }

        if (response.Destination is null || response.Destination != settings.AssertionConsumerUrl)
        {
            return Refuse("destination", "The response was addressed elsewhere.");
        }

        if (started is null)
        {
            return Refuse("unsolicited", "No sign-in was started from this browser.");
        }

        if (response.InResponseToAsString != started.Value.RequestId)
        {
            return Refuse("in_response_to", "The response answers another request.");
        }

        var assertionId = response.Saml2SecurityToken?.Assertion?.Id?.Value;
        if (string.IsNullOrEmpty(assertionId) || !pending.FirstUse(assertionId, response.SecurityTokenValidTo))
        {
            return Refuse("replay", "The assertion has been used before.");
        }

        await response.CreateSession(HttpContext, claimsTransform: Person.From);
        return LocalRedirect(started.Value.ReturnUrl);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromServices] IAntiforgery antiforgery)
    {
        // Only from this application's own form: another site must not be able
        // to sign a person out, or in general post here with their cookie.
        if (!await antiforgery.IsRequestValidAsync(HttpContext))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Sign-out refused", detail: "antiforgery");
        }

        // This application's session only. It does not sign the person out of
        // Keycloak (single logout is not implemented).
        await HttpContext.SignOutAsync(Saml2Constants.AuthenticationScheme);
        return LocalRedirect("/");
    }

    private IActionResult Refuse(string reason, string detail)
    {
        // A fixed word for the log and the page. Never the response itself.
        logger.LogWarning("saml response refused reason={Reason} detail={Detail}", reason, detail);
        return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Sign-in refused", detail: reason);
    }

    /// <summary>Only ever back into this application: never to a URL someone put in a link.</summary>
    private string LocalPath(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}

/// <summary>What this application keeps of a person, taken from a checked assertion.</summary>
public static class Person
{
    public const string Username = "reports:username";
    public const string Email = "reports:email";
    public const string Group = "reports:group";
    public const string Tenant = "reports:tenant";

    public static ClaimsPrincipal From(ClaimsPrincipal incoming)
    {
        var claims = new List<Claim>();
        Copy(incoming, ClaimTypes.NameIdentifier, ClaimTypes.NameIdentifier, claims);
        // What sign-out would need, kept by the library's own names.
        Copy(incoming, Saml2ClaimTypes.NameId, Saml2ClaimTypes.NameId, claims);
        Copy(incoming, Saml2ClaimTypes.NameIdFormat, Saml2ClaimTypes.NameIdFormat, claims);
        Copy(incoming, Saml2ClaimTypes.SessionIndex, Saml2ClaimTypes.SessionIndex, claims);
        Copy(incoming, "username", Username, claims);
        Copy(incoming, "email", Email, claims);

        var groups = incoming.FindAll("groups").Select(c => c.Value).Where(v => v.StartsWith('/')).Distinct().ToList();
        claims.AddRange(groups.Select(g => new Claim(Group, g)));

        // The tenant is the first segment of the group path, as in the API. A
        // person in two tenants is in none: this application does not guess.
        var tenants = groups.Select(g => g.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        if (tenants.Count == 1)
        {
            claims.Add(new Claim(Tenant, tenants[0]!));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, incoming.Identity?.AuthenticationType, Username, null));
    }

    private static void Copy(ClaimsPrincipal from, string type, string to, List<Claim> claims)
    {
        if (from.FindFirst(type) is { } claim)
        {
            claims.Add(new Claim(to, claim.Value));
        }
    }
}
