// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using DocVault.Reports.Tests.Support;
using Microsoft.Extensions.Configuration;

namespace DocVault.Reports.Tests.Security;

/// <summary>A setting that is missing or unsafe stops the start; it is not papered over.</summary>
public sealed class SettingsTests
{
    private static Dictionary<string, string?> Valid()
    {
        var (certificate, key) = TestIdp.PemKeyPair();
        return new Dictionary<string, string?>
        {
            ["Reports:BaseUrl"] = "http://localhost:5003",
            ["Saml:EntityId"] = "http://localhost:5003/saml",
            ["Saml:IdpEntityId"] = TestIdp.EntityId,
            ["Saml:IdpSignOnUrl"] = "https://idp.test/realms/docvault/protocol/saml",
            ["Saml:IdpCertificate"] = new TestIdp().CertificateBase64,
            ["Saml:SigningCertificate"] = certificate,
            ["Saml:SigningKey"] = key,
        };
    }

    private static ReportsSettings Load(Dictionary<string, string?> values) =>
        ReportsSettings.From(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Valid_settings_load()
    {
        var settings = Load(Valid());
        Assert.Equal("http://localhost:5003/saml/acs", settings.AssertionConsumerUrl.ToString());
        Assert.True(settings.SigningCertificate.HasPrivateKey);
        Assert.False(settings.IdpCertificate.HasPrivateKey);
    }

    [Theory]
    [InlineData("Reports:BaseUrl")]
    [InlineData("Saml:EntityId")]
    [InlineData("Saml:IdpEntityId")]
    [InlineData("Saml:IdpSignOnUrl")]
    [InlineData("Saml:IdpCertificate")]
    [InlineData("Saml:SigningCertificate")]
    [InlineData("Saml:SigningKey")]
    public void A_missing_setting_is_named(string name)
    {
        var values = Valid();
        values.Remove(name);
        var exception = Assert.Throws<InvalidOperationException>(() => Load(values));
        Assert.Contains(name, exception.Message);
    }

    [Theory]
    [InlineData("Reports:BaseUrl", "http://reports.example.com")]
    [InlineData("Saml:IdpSignOnUrl", "http://idp.example.com/realms/docvault/protocol/saml")]
    public void Plain_http_is_refused_off_the_loopback(string name, string value)
    {
        var values = Valid();
        values[name] = value;
        var exception = Assert.Throws<InvalidOperationException>(() => Load(values));
        Assert.Contains("https", exception.Message);
    }

    [Fact]
    public void A_key_that_does_not_match_the_certificate_is_refused_without_quoting_it()
    {
        var values = Valid();
        var (_, otherKey) = TestIdp.PemKeyPair();
        values["Saml:SigningKey"] = otherKey;

        var exception = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.DoesNotContain("PRIVATE KEY", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void An_identity_provider_certificate_that_is_not_one_is_refused()
    {
        var values = Valid();
        values["Saml:IdpCertificate"] = "bm90IGEgY2VydGlmaWNhdGU=";
        Assert.Throws<InvalidOperationException>(() => Load(values));
    }
}
