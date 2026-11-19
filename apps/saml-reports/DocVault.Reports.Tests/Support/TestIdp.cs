// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace DocVault.Reports.Tests.Support;

/// <summary>
/// An identity provider the tests control: it holds a signing key, so unlike
/// tools/assert-saml.py it can make a response that is properly signed and
/// still wrong (expired, for someone else, answering another request).
///
/// The XML is written by hand and signed with .NET's SignedXml, not with the
/// library the application uses, so that a mistake in one is not mirrored in
/// the other.
/// </summary>
public sealed class TestIdp
{
    public const string EntityId = "https://idp.test/realms/docvault";
    public const string Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    public const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";

    public X509Certificate2 Certificate { get; }

    public TestIdp(string subject = "CN=test-idp")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    public string CertificateBase64 => Convert.ToBase64String(Certificate.Export(X509ContentType.Cert));

    /// <summary>A key pair as PEM, for the application's own signing certificate.</summary>
    public static (string Certificate, string Key) PemKeyPair(string subject = "CN=reports")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    public sealed record Options
    {
        public string? InResponseTo { get; init; }

        /// <summary>What the unsigned envelope says it answers, when that differs from the signed assertion.</summary>
        public string? EnvelopeInResponseTo { get; init; }

        /// <summary>The signed recipient inside the assertion, when that differs from the envelope's Destination.</summary>
        public string? Recipient { get; init; }

        /// <summary>How the subject is confirmed. Keycloak sends bearer.</summary>
        public string ConfirmationMethod { get; init; } = "urn:oasis:names:tc:SAML:2.0:cm:bearer";

        /// <summary>A second username attribute, placed before the real one.</summary>
        public string? UsernameBefore { get; init; }
        public string Destination { get; init; } = "http://localhost:5003/saml/acs";
        public string Audience { get; init; } = "http://localhost:5003/saml";
        public string Issuer { get; init; } = EntityId;
        public string Username { get; init; } = "alice";
        public string Email { get; init; } = "alice@acme.test";
        public string[] Groups { get; init; } = ["/acme/engineering"];
        public string AssertionId { get; init; } = "_" + Guid.NewGuid().ToString("N");
        public DateTimeOffset NotBefore { get; init; } = DateTimeOffset.UtcNow.AddMinutes(-1);
        public DateTimeOffset NotOnOrAfter { get; init; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public string Status { get; init; } = "urn:oasis:names:tc:SAML:2.0:status:Success";
        public bool SignAssertion { get; init; } = true;
        public bool SignResponse { get; init; } = true;
    }

    /// <summary>A response as XML. Signed by this provider unless the options say otherwise.</summary>
    public XmlDocument Response(Options options)
    {
        var now = Instant(DateTimeOffset.UtcNow);
        var inResponseTo = options.InResponseTo is null ? "" : $" InResponseTo=\"{options.InResponseTo}\"";
        var envelopeInResponseTo = (options.EnvelopeInResponseTo ?? options.InResponseTo) is { } envelope
            ? $" InResponseTo=\"{envelope}\""
            : "";
        var recipient = options.Recipient ?? options.Destination;
        var groups = string.Concat(options.Groups.Select(g =>
            $"<saml:AttributeValue xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"xs:string\">{g}</saml:AttributeValue>"));
        var groupsAttribute = options.Groups.Length == 0
            ? ""
            : $"<saml:Attribute Name=\"groups\" NameFormat=\"urn:oasis:names:tc:SAML:2.0:attrname-format:basic\">{groups}</saml:Attribute>";

        var xml =
            $"<samlp:Response xmlns:samlp=\"{Protocol}\" xmlns:saml=\"{Assertion}\" ID=\"_{Guid.NewGuid():N}\" Version=\"2.0\" IssueInstant=\"{now}\" Destination=\"{options.Destination}\"{envelopeInResponseTo}>" +
            $"<saml:Issuer>{options.Issuer}</saml:Issuer>" +
            $"<samlp:Status><samlp:StatusCode Value=\"{options.Status}\"/></samlp:Status>" +
            $"<saml:Assertion ID=\"{options.AssertionId}\" Version=\"2.0\" IssueInstant=\"{now}\">" +
            $"<saml:Issuer>{options.Issuer}</saml:Issuer>" +
            "<saml:Subject>" +
            "<saml:NameID Format=\"urn:oasis:names:tc:SAML:2.0:nameid-format:persistent\">G-0f1e2d3c</saml:NameID>" +
            $"<saml:SubjectConfirmation Method=\"{options.ConfirmationMethod}\">" +
            $"<saml:SubjectConfirmationData{inResponseTo} NotOnOrAfter=\"{Instant(options.NotOnOrAfter)}\" Recipient=\"{recipient}\"/>" +
            "</saml:SubjectConfirmation></saml:Subject>" +
            $"<saml:Conditions NotBefore=\"{Instant(options.NotBefore)}\" NotOnOrAfter=\"{Instant(options.NotOnOrAfter)}\">" +
            $"<saml:AudienceRestriction><saml:Audience>{options.Audience}</saml:Audience></saml:AudienceRestriction></saml:Conditions>" +
            $"<saml:AuthnStatement AuthnInstant=\"{now}\" SessionIndex=\"session-1\"><saml:AuthnContext>" +
            "<saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:unspecified</saml:AuthnContextClassRef>" +
            "</saml:AuthnContext></saml:AuthnStatement>" +
            "<saml:AttributeStatement>" +
            (options.UsernameBefore is null ? "" : Attribute("username", options.UsernameBefore)) +
            Attribute("username", options.Username) + Attribute("email", options.Email) + groupsAttribute +
            "</saml:AttributeStatement>" +
            "</saml:Assertion></samlp:Response>";

        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        if (options.SignAssertion)
        {
            Sign(document, (XmlElement)document.GetElementsByTagName("Assertion", Assertion)[0]!, Certificate);
        }

        if (options.SignResponse)
        {
            Sign(document, document.DocumentElement!, Certificate);
        }

        return document;
    }

    public static string Encode(XmlDocument document) => Convert.ToBase64String(Encoding.UTF8.GetBytes(document.OuterXml));

    public string Encoded(Options options) => Encode(Response(options));

    /// <summary>Sign an element in place; the signature goes right after its Issuer, where SAML wants it.</summary>
    public static void Sign(XmlDocument document, XmlElement element, X509Certificate2 certificate)
    {
        var signed = new SignedXml(element) { SigningKey = certificate.GetRSAPrivateKey() };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference("#" + element.GetAttribute("ID")) { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();

        var issuer = element.GetElementsByTagName("Issuer", Assertion)[0]!;
        element.InsertAfter(document.ImportNode(signed.GetXml(), deep: true), issuer);
    }

    private static string Attribute(string name, string value) =>
        $"<saml:Attribute Name=\"{name}\" NameFormat=\"urn:oasis:names:tc:SAML:2.0:attrname-format:basic\">" +
        $"<saml:AttributeValue xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"xs:string\">{value}</saml:AttributeValue></saml:Attribute>";

    private static string Instant(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
