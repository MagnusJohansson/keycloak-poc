using Microsoft.Extensions.Configuration;

namespace DocVault.Desktop.Auth;

/// <summary>
/// Where the desktop client points: which Keycloak, which API, which client id.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately lives in this cross-platform library rather than in the WinUI app,
/// so it can be unit-tested on any operating system. The UI project only calls
/// <see cref="Load"/>.
/// </para>
/// <para>
/// Sources, later overriding earlier:
/// <list type="number">
///   <item>built-in defaults — the local Docker lab</item>
///   <item><c>appsettings.json</c> next to the executable</item>
///   <item>environment variables prefixed <c>DOCVAULT_</c></item>
/// </list>
/// A file is the primary mechanism because environment variables are awkward for
/// a GUI app: there is nowhere to set them when it is launched from the Start
/// menu or Explorer. The environment override remains for CI and for running two
/// instances against different realms.
/// </para>
/// </remarks>
public sealed record DesktopSettings
{
    /// <summary>The realm issuer, e.g. https://your-keycloak/realms/docvault</summary>
    public string Authority { get; init; } = "http://localhost:8080/realms/docvault";

    public string ApiBaseUrl { get; init; } = "http://localhost:5001";

    /// <summary>Must match a client registered in the realm (see 20-realm/clients.tf).</summary>
    public string ClientId { get; init; } = "docvault-winui";

    public string Scope { get; init; } = "openid profile email";

    /// <summary>The ACR the API demands for classified documents.</summary>
    public string StepUpAcr { get; init; } = "silver";

    public static DesktopSettings Load(string? basePath = null)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath ?? AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)

            // Strips the prefix, so DOCVAULT_AUTHORITY binds to Authority. Keys are
            // case-insensitive, so DOCVAULT_APIBASEURL works too.
            .AddEnvironmentVariables("DOCVAULT_")
            .Build();

        var settings = configuration.Get<DesktopSettings>() ?? new DesktopSettings();
        settings.Validate();
        return settings;
    }

    /// <summary>
    /// Fails fast on configuration that cannot possibly work, rather than surfacing it
    /// later as an opaque browser or token error.
    /// </summary>
    public void Validate()
    {
        // The scheme check is not redundant: Uri.TryCreate happily parses
        // "localhost:8080/realms/docvault" as absolute, treating "localhost" as the
        // SCHEME. Without this, a missing https:// sails through validation and fails
        // much later as an unhelpful discovery error.
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority)
            || (authority.Scheme != Uri.UriSchemeHttp && authority.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Authority '{Authority}' is not an absolute http(s) URL. Expected something like " +
                "https://your-keycloak/realms/docvault.");
        }

        // A realm issuer always ends in /realms/<name>. Pointing at the host root is a
        // common slip and produces a 404 on discovery that reads like the server is down.
        if (!authority.AbsolutePath.Contains("/realms/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Authority '{Authority}' does not look like a realm issuer. It should include " +
                "/realms/<realm>, e.g. https://your-keycloak/realms/docvault.");
        }

        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var api)
            || (api.Scheme != Uri.UriSchemeHttp && api.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"ApiBaseUrl '{ApiBaseUrl}' is not an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException("ClientId must be set.");
        }
    }

    /// <summary>Projects onto the options the OIDC client actually needs.</summary>
    public DesktopAuthOptions ToAuthOptions() => new()
    {
        Authority = Authority,
        ClientId = ClientId,
        Scope = Scope,
        StepUpAcr = StepUpAcr,
    };
}
