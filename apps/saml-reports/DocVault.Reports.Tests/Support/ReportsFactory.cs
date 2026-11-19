// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocVault.Reports.Tests.Support;

/// <summary>The application, in memory, trusting <see cref="TestIdp"/> instead of Keycloak.</summary>
public sealed class ReportsFactory : WebApplicationFactory<Program>
{
    public TestIdp Idp { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var (certificate, key) = TestIdp.PemKeyPair();
        builder.UseSetting("Reports:BaseUrl", "http://localhost:5003");
        builder.UseSetting("Saml:EntityId", "http://localhost:5003/saml");
        builder.UseSetting("Saml:IdpEntityId", TestIdp.EntityId);
        builder.UseSetting("Saml:IdpSignOnUrl", "https://idp.test/realms/docvault/protocol/saml");
        builder.UseSetting("Saml:IdpCertificate", Idp.CertificateBase64);
        builder.UseSetting("Saml:SigningCertificate", certificate);
        builder.UseSetting("Saml:SigningKey", key);
    }

    public Browser NewBrowser() => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
        BaseAddress = new Uri("http://localhost:5003"),
    }));
}

/// <summary>
/// One browser: a cookie jar and the requests a person's browser would make.
/// Cookies are kept by hand because the sign-in cookie is marked Secure and
/// the test server is plain HTTP, where HttpClient's own jar would drop it.
/// </summary>
public sealed class Browser(HttpClient client)
{
    public Dictionary<string, string> Cookies { get; } = [];

    /// <summary>The Set-Cookie lines of the last response, as sent.</summary>
    public List<string> LastSetCookies { get; private set; } = [];

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, IDictionary<string, string>? form = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (Cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        var response = await client.SendAsync(request);
        LastSetCookies = response.Headers.TryGetValues("Set-Cookie", out var lines) ? [.. lines] : [];
        foreach (var line in LastSetCookies)
        {
            var pair = line.Split(';', 2)[0];
            var name = pair[..pair.IndexOf('=')];
            var value = pair[(pair.IndexOf('=') + 1)..];
            if (value.Length == 0 || line.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
            {
                Cookies.Remove(name);
            }
            else
            {
                Cookies[name] = value;
            }
        }

        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    /// <summary>Start a sign-in. Returns the id of the request the application made, and the request itself.</summary>
    public async Task<(string RequestId, string RequestXml, string Body)> StartSignInAsync(string path = "/saml/login")
    {
        var response = await GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var encoded = Regex.Match(body, "name=\"SAMLRequest\" value=\"([^\"]+)\"").Groups[1].Value;
        var xml = Encoding.UTF8.GetString(Convert.FromBase64String(WebUtility.HtmlDecode(encoded)));
        var id = Regex.Match(xml, " ID=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(id);
        return (id, xml, body);
    }

    public Task<HttpResponseMessage> PostResponseAsync(string samlResponse) =>
        SendAsync(HttpMethod.Post, "/saml/acs", new Dictionary<string, string> { ["SAMLResponse"] = samlResponse });

    /// <summary>Sign in with a response the provider signs for this browser's own request.</summary>
    public async Task<HttpResponseMessage> SignInAsync(TestIdp idp, Func<TestIdp.Options, TestIdp.Options>? change = null)
    {
        var (requestId, _, _) = await StartSignInAsync();
        var options = new TestIdp.Options { InResponseTo = requestId };
        return await PostResponseAsync(idp.Encoded(change is null ? options : change(options)));
    }

    public async Task<JsonElement> MeAsync()
    {
        var response = await GetAsync("/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>The fixed word the application gave for a refusal.</summary>
    public static async Task<string?> ReasonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("detail", out var detail)
            ? detail.GetString()
            : null;
}
