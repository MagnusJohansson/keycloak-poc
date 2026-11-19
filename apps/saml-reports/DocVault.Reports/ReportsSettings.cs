// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace DocVault.Reports;

/// <summary>
/// Everything this application needs to know about itself and about the
/// identity provider it trusts. Read once at startup; a value that is missing
/// or unsafe stops the start, with a message that says which one.
/// </summary>
public sealed class ReportsSettings
{
    /// <summary>Where this application is reached, e.g. http://localhost:5003.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>This application's SAML name. Keycloak knows it as the client id.</summary>
    public required string EntityId { get; init; }

    /// <summary>The identity provider's SAML name: the realm's URL.</summary>
    public required string IdpEntityId { get; init; }

    /// <summary>Where sign-in requests are sent.</summary>
    public required Uri IdpSignOnUrl { get; init; }

    /// <summary>The certificate the identity provider signs with. Pinned, not fetched.</summary>
    public required X509Certificate2 IdpCertificate { get; init; }

    /// <summary>The key pair this application signs its requests with.</summary>
    public required X509Certificate2 SigningCertificate { get; init; }

    public Uri AssertionConsumerUrl => new(BaseUrl, "/saml/acs");

    public static ReportsSettings From(IConfiguration configuration)
    {
        var baseUrl = Url(configuration, "Reports:BaseUrl");
        var idpSignOn = Url(configuration, "Saml:IdpSignOnUrl");

        // A SAML response is posted here through the user's browser. Over plain
        // HTTP anyone on the path can read it, and it is a credential until it
        // expires. Loopback is the lab.
        foreach (var (name, url) in new[] { ("Reports:BaseUrl", baseUrl), ("Saml:IdpSignOnUrl", idpSignOn) })
        {
            if (url.Scheme != Uri.UriSchemeHttps && !IsLoopback(url))
            {
                throw new InvalidOperationException($"{name} must be https, or a loopback address for local development.");
            }
        }

        return new ReportsSettings
        {
            BaseUrl = baseUrl,
            EntityId = Required(configuration, "Saml:EntityId"),
            IdpEntityId = Required(configuration, "Saml:IdpEntityId"),
            IdpSignOnUrl = idpSignOn,
            IdpCertificate = PublicCertificate(Required(configuration, "Saml:IdpCertificate"), "Saml:IdpCertificate"),
            SigningCertificate = KeyPair(
                Required(configuration, "Saml:SigningCertificate"), Required(configuration, "Saml:SigningKey")),
        };
    }

    private static string Required(IConfiguration configuration, string name) =>
        configuration[name] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set. `make reports` sets it from the realm's Terraform outputs.");

    private static Uri Url(IConfiguration configuration, string name) =>
        Uri.TryCreate(Required(configuration, name), UriKind.Absolute, out var url)
            ? url
            : throw new InvalidOperationException($"{name} is not an absolute URL.");

    private static bool IsLoopback(Uri url) =>
        url.IsLoopback || (IPAddress.TryParse(url.Host, out var address) && IPAddress.IsLoopback(address));

    /// <summary>A certificate as Keycloak and Terraform hand it over: PEM, or the bare base64 inside one.</summary>
    internal static X509Certificate2 PublicCertificate(string value, string name)
    {
        try
        {
            return value.Contains("-----BEGIN", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(value)
                : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value));
        }
        catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException($"{name} is not a certificate.", exception);
        }
    }

    private static X509Certificate2 KeyPair(string certificatePem, string keyPem)
    {
        try
        {
            return X509Certificate2.CreateFromPem(certificatePem, keyPem);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            // Not the exception's own text: it can quote the key.
            throw new InvalidOperationException("Saml:SigningCertificate and Saml:SigningKey are not a matching PEM key pair.");
        }
    }
}
