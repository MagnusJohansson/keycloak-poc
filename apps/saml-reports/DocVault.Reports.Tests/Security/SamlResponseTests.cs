// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Xml;
using DocVault.Reports.Tests.Support;

namespace DocVault.Reports.Tests.Security;

/// <summary>
/// What the application does with a SAML response. A sign-in that works is one
/// test; the rest are responses it must refuse, including ones that are
/// correctly signed by the provider it trusts.
/// </summary>
public sealed class SamlResponseTests(ReportsFactory factory) : IClassFixture<ReportsFactory>
{
    private TestIdp Idp => factory.Idp;

    private static async Task Refused(HttpResponseMessage response, string reason, Browser browser)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(reason, await Browser.ReasonAsync(response));
        // And no session came of it.
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task A_signed_response_to_this_browsers_request_signs_the_person_in()
    {
        var browser = factory.NewBrowser();

        var response = await browser.SignInAsync(Idp);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var me = await browser.MeAsync();
        Assert.Equal("alice", me.GetProperty("username").GetString());
        Assert.Equal("alice@acme.test", me.GetProperty("email").GetString());
        Assert.Equal("acme", me.GetProperty("tenant").GetString());
        Assert.Equal("/acme/engineering", me.GetProperty("groups")[0].GetString());
    }

    [Fact]
    public async Task Without_a_session_the_person_is_sent_to_sign_in_and_health_is_open()
    {
        var browser = factory.NewBrowser();

        var me = await browser.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Redirect, me.StatusCode);
        Assert.StartsWith("/saml/login", me.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/reports/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task The_request_is_signed_posted_to_the_provider_and_tied_to_this_browser()
    {
        var browser = factory.NewBrowser();

        var (_, xml, body) = await browser.StartSignInAsync();

        Assert.Contains("action=\"https://idp.test/realms/docvault/protocol/saml\"", body);
        Assert.Contains("<Signature", xml);
        Assert.Contains("AssertionConsumerServiceURL=\"http://localhost:5003/saml/acs\"", xml);
        Assert.Contains(">http://localhost:5003/saml<", xml); // the Issuer: who is asking
        var cookie = Assert.Single(browser.LastSetCookies, c => c.StartsWith("reports.signin=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);
        // The request id is in the cookie, and not readable there.
        Assert.DoesNotContain(browser.Cookies["reports.signin"], xml);
    }

    // --- signatures ---------------------------------------------------------

    [Fact]
    public async Task A_response_with_no_signature_is_refused()
    {
        var browser = factory.NewBrowser();
        var response = await browser.SignInAsync(Idp, o => o with { SignAssertion = false, SignResponse = false });
        await Refused(response, "invalid", browser);
    }

    [Fact]
    public async Task A_response_signed_by_another_key_is_refused()
    {
        var browser = factory.NewBrowser();
        var response = await browser.SignInAsync(new TestIdp("CN=not-the-provider"));
        await Refused(response, "invalid", browser);
    }

    [Fact]
    public async Task A_response_changed_after_signing_is_refused()
    {
        var browser = factory.NewBrowser();
        var (requestId, _, _) = await browser.StartSignInAsync();
        var document = Idp.Response(new TestIdp.Options { InResponseTo = requestId, Username = "bob", Groups = ["/globex"] });
        var changed = document.OuterXml.Replace(">bob<", ">alice<").Replace(">/globex<", ">/acme/engineering<");

        var response = await browser.PostResponseAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(changed)));

        await Refused(response, "invalid", browser);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_forged_assertion_beside_the_signed_one_does_not_sign_its_person_in(bool forgedFirst)
    {
        // Signature wrapping: the response carries the genuine signed assertion
        // for bob, and an unsigned one for alice. Whatever the application
        // makes of that, it must not be alice.
        var browser = factory.NewBrowser();
        var (requestId, _, _) = await browser.StartSignInAsync();
        var document = Idp.Response(new TestIdp.Options
        {
            InResponseTo = requestId, Username = "bob", Email = "bob@globex.test", Groups = ["/globex"], SignResponse = false,
        });
        var genuine = (XmlElement)document.GetElementsByTagName("Assertion", TestIdp.Assertion)[0]!;
        var forged = (XmlElement)genuine.CloneNode(deep: true);
        forged.RemoveChild(forged.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#")[0]!);
        forged.SetAttribute("ID", "_forged");
        forged.InnerXml = forged.InnerXml.Replace(">bob<", ">alice<").Replace(">/globex<", ">/acme/engineering<");
        if (forgedFirst)
        {
            document.DocumentElement!.InsertBefore(forged, genuine);
        }
        else
        {
            document.DocumentElement!.InsertAfter(forged, genuine);
        }

        var response = await browser.PostResponseAsync(TestIdp.Encode(document));

        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            Assert.Equal("bob", (await browser.MeAsync()).GetProperty("username").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    // --- correctly signed, and still wrong ----------------------------------

    [Fact]
    public async Task A_signed_response_for_another_application_is_refused()
    {
        var browser = factory.NewBrowser();
        var response = await browser.SignInAsync(Idp, o => o with { Audience = "https://another-application.example/saml" });
        await Refused(response, "invalid", browser);
    }

    [Fact]
    public async Task A_signed_response_addressed_to_another_endpoint_is_refused()
    {
        var browser = factory.NewBrowser();
        var response = await browser.SignInAsync(Idp, o => o with { Destination = "https://another-application.example/saml/acs" });
        await Refused(response, "destination", browser);
    }

    [Fact]
    public async Task A_signed_response_from_another_issuer_is_refused()
    {
        var browser = factory.NewBrowser();
        var response = await browser.SignInAsync(Idp, o => o with { Issuer = "https://idp.test/realms/another-realm" });
        await Refused(response, "invalid", browser);
    }

    [Fact]
    public async Task An_expired_assertion_is_refused()
    {
        var browser = factory.NewBrowser();
        var response = await browser.SignInAsync(Idp, o => o with
        {
            NotBefore = DateTimeOffset.UtcNow.AddHours(-2), NotOnOrAfter = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await Refused(response, "invalid", browser);
    }

    [Fact]
    public async Task An_assertion_that_is_not_valid_yet_is_refused()
    {
        var browser = factory.NewBrowser();
        var response = await browser.SignInAsync(Idp, o => o with
        {
            NotBefore = DateTimeOffset.UtcNow.AddHours(1), NotOnOrAfter = DateTimeOffset.UtcNow.AddHours(2),
        });
        await Refused(response, "invalid", browser);
    }

    [Fact]
    public async Task A_response_that_reports_failure_is_refused()
    {
        var browser = factory.NewBrowser();
        var response = await browser.SignInAsync(Idp, o => o with { Status = "urn:oasis:names:tc:SAML:2.0:status:Responder" });
        await Refused(response, "status", browser);
    }

    // --- the right response in the wrong place ------------------------------

    [Fact]
    public async Task A_signed_response_nobody_asked_for_is_refused()
    {
        var browser = factory.NewBrowser(); // never started a sign-in
        var response = await browser.PostResponseAsync(Idp.Encoded(new TestIdp.Options()));
        await Refused(response, "unsolicited", browser);
    }

    [Fact]
    public async Task A_response_to_someone_elses_request_is_refused()
    {
        // Alice signs in; an attacker who got hold of her response posts it
        // from a browser of their own, mid-way through their own sign-in.
        var alice = factory.NewBrowser();
        var (aliceRequest, _, _) = await alice.StartSignInAsync();
        var aliceResponse = Idp.Encoded(new TestIdp.Options { InResponseTo = aliceRequest });

        var attacker = factory.NewBrowser();
        await attacker.StartSignInAsync();
        var response = await attacker.PostResponseAsync(aliceResponse);

        await Refused(response, "in_response_to", attacker);
    }

    [Fact]
    public async Task The_same_response_is_not_accepted_twice()
    {
        var browser = factory.NewBrowser();
        var (requestId, _, _) = await browser.StartSignInAsync();
        var signInCookie = browser.Cookies["reports.signin"];
        var samlResponse = Idp.Encoded(new TestIdp.Options { InResponseTo = requestId });
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostResponseAsync(samlResponse)).StatusCode);

        // A second browser with a copy of both the response and the cookie.
        var copy = factory.NewBrowser();
        copy.Cookies["reports.signin"] = signInCookie;
        var response = await copy.PostResponseAsync(samlResponse);

        await Refused(response, "replay", copy);
    }

    [Fact]
    public async Task A_sign_in_cookie_the_browser_made_up_is_not_believed()
    {
        var browser = factory.NewBrowser();
        browser.Cookies["reports.signin"] = "bm90LW91cnM";
        var response = await browser.PostResponseAsync(Idp.Encoded(new TestIdp.Options { InResponseTo = "_made-up" }));
        await Refused(response, "unsolicited", browser);
    }

    // --- after sign-in ------------------------------------------------------

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task Sign_in_never_ends_at_an_address_outside_the_application(string returnUrl)
    {
        var browser = factory.NewBrowser();
        var (requestId, _, _) = await browser.StartSignInAsync("/saml/login?returnUrl=" + Uri.EscapeDataString(returnUrl));

        var response = await browser.PostResponseAsync(Idp.Encoded(new TestIdp.Options { InResponseTo = requestId }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Sign_in_returns_to_the_page_that_was_asked_for()
    {
        var browser = factory.NewBrowser();
        var (requestId, _, _) = await browser.StartSignInAsync("/saml/login?returnUrl=%2Freports%2Fsummary");

        var response = await browser.PostResponseAsync(Idp.Encoded(new TestIdp.Options { InResponseTo = requestId }));

        Assert.Equal("/reports/summary", response.Headers.Location?.OriginalString);
        var report = await browser.GetAsync("/reports/summary");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Contains("\"tenant\":\"acme\"", await report.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_person_in_no_tenant_is_signed_in_and_forbidden_the_report()
    {
        var browser = factory.NewBrowser();
        await browser.SignInAsync(Idp, o => o with { Username = "dave", Groups = [] });

        Assert.Equal("dave", (await browser.MeAsync()).GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/reports/summary")).StatusCode);
    }

    [Fact]
    public async Task A_person_in_two_tenants_is_in_none()
    {
        var browser = factory.NewBrowser();
        await browser.SignInAsync(Idp, o => o with { Groups = ["/acme/engineering", "/globex"] });

        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await browser.MeAsync()).GetProperty("tenant").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/reports/summary")).StatusCode);
    }

    [Fact]
    public async Task Signing_out_needs_the_applications_own_form()
    {
        var browser = factory.NewBrowser();
        await browser.SignInAsync(Idp);

        // A POST from somewhere else, with the session cookie but no anti-forgery token.
        var response = await browser.SendAsync(HttpMethod.Post, "/saml/logout", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("alice", (await browser.MeAsync()).GetProperty("username").GetString());
    }

    [Fact]
    public async Task Signing_out_from_the_applications_own_page_ends_the_session()
    {
        var browser = factory.NewBrowser();
        await browser.SignInAsync(Idp);
        var page = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();
        var field = System.Text.RegularExpressions.Regex.Match(page, "type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\"");

        var response = await browser.SendAsync(HttpMethod.Post, "/saml/logout", new Dictionary<string, string>
        {
            [field.Groups[1].Value] = WebUtility.HtmlDecode(field.Groups[2].Value),
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task The_page_escapes_what_the_assertion_said()
    {
        var browser = factory.NewBrowser();
        await browser.SignInAsync(Idp, o => o with { Username = "&lt;script&gt;alert(1)&lt;/script&gt;" });

        var page = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert(1)</script>", page);
        Assert.Contains("&lt;script&gt;", page);
    }
}
