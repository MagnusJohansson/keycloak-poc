// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Claims;
using System.ServiceModel.Security;
using DocVault.Reports;
using DocVault.Reports.Saml;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.MvcCore.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

// ---------------------------------------------------------------------------
// DocVault Reports: a stand-in for a legacy application that only speaks SAML.
//
// It has no idea what a JWT is and never calls the DocVault API. People sign in
// to it through Keycloak, which here plays the SAML identity provider. That is
// the other half of moving off a legacy federation: the old applications keep
// working while the people move.
//
// See docs/use-cases/uc9-saml-application.md.
// ---------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);

var settings = ReportsSettings.From(builder.Configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<PendingSignIn>();

builder.Services.AddSingleton(_ =>
{
    var saml = new Saml2Configuration
    {
        Issuer = settings.EntityId,
        SingleSignOnDestination = settings.IdpSignOnUrl,
        AllowedIssuer = settings.IdpEntityId,

        // What this application sends is signed with its own key.
        SigningCertificate = settings.SigningCertificate,
        SignAuthnRequest = true,
        SignatureAlgorithm = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256",

        // What it accepts must be signed by the one pinned certificate, and
        // addressed to this application. The certificate is self-signed, so it
        // is trusted because it is this one, not because of who issued it.
        CertificateValidationMode = X509CertificateValidationMode.None,
        RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
        AudienceRestricted = true,
    };
    saml.SignatureValidationCertificates.Add(settings.IdpCertificate);
    saml.AllowedAudienceUris.Add(settings.EntityId);
    return saml;
});

// The session cookie: this application's own, for one browser session.
builder.Services.AddSaml2(loginPath: "/saml/login", slidingExpiration: false);
builder.Services.Configure<CookieAuthenticationOptions>(Saml2Constants.AuthenticationScheme, options =>
{
    // Never over plain HTTP. (Browsers accept a Secure cookie on localhost.)
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;

    // Signed in and not allowed is a 403, said plainly. The default sends the
    // person to an "access denied" page this application does not have.
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(options =>
{
    // Deny by default: an endpoint is reachable without a session only if it says so.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("InATenant", policy => policy.RequireClaim(Person.Tenant));
});
builder.Services.AddControllers();
builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseSaml2();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// Who the assertion said you are. An application that speaks SAML knows
// nothing else about you.
app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
{
    username = user.FindFirstValue(Person.Username),
    email = user.FindFirstValue(Person.Email),
    groups = user.FindAll(Person.Group).Select(c => c.Value),
    tenant = user.FindFirstValue(Person.Tenant),
}));

// The "report": a number per tenant. A signed-in person with no tenant gets a
// 403, not a 401 and not somebody else's number.
app.MapGet("/reports/summary", (ClaimsPrincipal user) =>
{
    var tenant = user.FindFirstValue(Person.Tenant)!;
    return Results.Ok(new { tenant, report = "document summary", documents = Reports.DocumentsOf(tenant) });
}).RequireAuthorization("InATenant");

app.MapGet("/", (ClaimsPrincipal user, HttpContext context, IAntiforgery antiforgery) =>
{
    var token = antiforgery.GetAndStoreTokens(context);
    return Results.Content(
        "<!doctype html><title>DocVault Reports</title><h1>DocVault Reports</h1>" +
        $"<p>Signed in as <b data-testid=\"username\">{System.Net.WebUtility.HtmlEncode(user.FindFirstValue(Person.Username))}</b>, " +
        $"tenant <b data-testid=\"tenant\">{System.Net.WebUtility.HtmlEncode(user.FindFirstValue(Person.Tenant) ?? "none")}</b>.</p>" +
        "<p><a href=\"/reports/summary\">Summary report</a> · <a href=\"/me\">What the assertion said</a></p>" +
        "<form method=\"post\" action=\"/saml/logout\">" +
        $"<input type=\"hidden\" name=\"{token.FormFieldName}\" value=\"{System.Net.WebUtility.HtmlEncode(token.RequestToken)}\">" +
        "<button>Sign out of Reports</button></form>",
        "text/html");
});

app.Run();

/// <summary>Stand-in data. The point of this application is how you get in.</summary>
internal static class Reports
{
    public static int DocumentsOf(string tenant) => tenant switch
    {
        "acme" => 2,
        "globex" => 1,
        _ => 0,
    };
}

public partial class Program;
