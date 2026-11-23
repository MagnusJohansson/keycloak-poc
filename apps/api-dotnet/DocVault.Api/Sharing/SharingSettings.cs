// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

namespace DocVault.Api.Sharing;

/// <summary>Who decides whether a caller may see a particular document.</summary>
public enum SharingMode
{
    /// <summary>
    /// Unset. Refused at startup: there is no safe default. Defaulting to
    /// <see cref="TenantOnly"/> would quietly turn every private document into one the
    /// whole tenant can read; defaulting to <see cref="OpenFga"/> would fail later, on
    /// the first request, instead of now.
    /// </summary>
    Unset,

    /// <summary>OpenFGA decides, per document. Private documents and sharing work.</summary>
    OpenFga,

    /// <summary>
    /// Everyone in a tenant sees every document of it: the lab before sharing existed,
    /// and what Azure runs, where there is no OpenFGA. Private documents are refused,
    /// not created and then shown to everyone.
    /// </summary>
    TenantOnly,
}

/// <summary>The <c>Sharing</c> section: <c>Sharing__Mode</c> and friends in the environment.</summary>
public sealed class SharingSettings
{
    public const string Section = "Sharing";

    public SharingMode Mode { get; set; }

    /// <summary>OpenFGA's HTTP API, e.g. <c>http://localhost:8090</c>.</summary>
    public string? OpenFgaUrl { get; set; }

    /// <summary>A preshared key OpenFGA accepts.</summary>
    public string? OpenFgaApiKey { get; set; }

    /// <summary>The store's id: <c>terraform output store_id</c> in 25-openfga.</summary>
    public string? StoreId { get; set; }

    /// <summary>
    /// The authorization model the API decides by: <c>terraform output model_id</c>. Pinned,
    /// never "the latest", so that writing a new model does not change a running API's
    /// decisions under it.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>The Keycloak client that looks people up for a share (20-realm/directory.tf).</summary>
    public string DirectoryClientId { get; set; } = "docvault-api-directory";

    public string? DirectoryClientSecret { get; set; }

    /// <summary>
    /// The least time a share to a person takes to answer, found or not. Looking someone
    /// up takes longer when they exist, and longer still when they come from the LDAP
    /// directory (their groups are read from it): measured at ~300 ms against ~90 ms for
    /// nobody, which told an owner who exists in another tenant even though both answers
    /// are the same 404 (found by review). One second is above the directory's slowest
    /// lookups in the lab; sharing is rare enough to afford it.
    /// </summary>
    public TimeSpan PersonLookupFloor { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Every problem with these settings, so that one start reports all of them.</summary>
    public IEnumerable<string> Problems()
    {
        switch (Mode)
        {
            case SharingMode.Unset:
                yield return "Sharing:Mode must be set: OpenFga (the local lab: make api sets it) or TenantOnly (no OpenFGA).";
                yield break;
            case SharingMode.TenantOnly:
                yield break;
        }

        foreach (var (name, value) in new[]
        {
            ("Sharing:OpenFgaUrl", OpenFgaUrl), ("Sharing:OpenFgaApiKey", OpenFgaApiKey),
            ("Sharing:StoreId", StoreId), ("Sharing:ModelId", ModelId),
            ("Sharing:DirectoryClientSecret", DirectoryClientSecret),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return $"{name} must be set when Sharing:Mode is OpenFga.";
            }
        }

        if (OpenFgaUrl is not null)
        {
            if (!Uri.TryCreate(OpenFgaUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                // TryCreate alone accepts "localhost:8090", reading "localhost" as the scheme.
                yield return "Sharing:OpenFgaUrl must be an absolute http(s) URL.";
            }
            else if (url.Scheme == "http" && !url.IsLoopback)
            {
                // The key travels in every request, and every decision travels back.
                yield return $"Sharing:OpenFgaUrl '{OpenFgaUrl}' is plain HTTP to a host that is not loopback. Use HTTPS.";
            }
        }
    }
}
