using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Microsoft.Extensions.Logging;

namespace DocVault.Desktop.Auth;

public sealed record DesktopAuthOptions
{
    /// <summary>e.g. http://localhost:8080/realms/docvault — the realm issuer.</summary>
    public required string Authority { get; init; }

    public string ClientId { get; init; } = "docvault-winui";
    public string Scope { get; init; } = "openid profile email";

    /// <summary>The step-up level the API asks for. Maps to LoA 2 via the realm's acr.loa.map.</summary>
    public string StepUpAcr { get; init; } = "silver";
}

/// <summary>
/// Signs a desktop user in to Keycloak using authorization code + PKCE via the system browser.
/// </summary>
/// <remarks>
/// <para>
/// Built on <c>Duende.IdentityModel.OidcClient</c>, a certified OpenID Connect relying party that
/// implements RFC 8252. PKCE is applied automatically and cannot be switched off — which is
/// correct for a public client, since a desktop app cannot keep a secret and the code verifier is
/// the only thing binding an authorization code to this application.
/// </para>
/// <para>
/// Everything here is plain OIDC, so nothing in this class is Keycloak-specific beyond the
/// default client id. It would work unchanged against any compliant provider.
/// </para>
/// </remarks>
public sealed class KeycloakDesktopClient
{
    private readonly DesktopAuthOptions _options;
    private readonly ITokenStore _store;
    private readonly OidcClient _client;
    private readonly LoopbackBrowser _browser;
    private readonly ILogger<KeycloakDesktopClient> _log;

    public KeycloakDesktopClient(DesktopAuthOptions options, ITokenStore store, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _store = store;
        _log = (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
            .CreateLogger<KeycloakDesktopClient>();
        _browser = new LoopbackBrowser(loggerFactory: loggerFactory);

        // The three values behind almost every sign-in failure. Logged before
        // anything is attempted, so a failed run says what it was trying to do.
        _log.LogInformation("Authority   {Authority}", options.Authority);
        _log.LogInformation("ClientId    {ClientId}", options.ClientId);
        _log.LogInformation("RedirectUri {RedirectUri}", _browser.RedirectUri);

        _client = new OidcClient(new OidcClientOptions
        {
            Authority = options.Authority,
            ClientId = options.ClientId,

            // The port is chosen when LoopbackBrowser is constructed, so the value registered
            // here and the socket actually listening always agree.
            RedirectUri = _browser.RedirectUri,
            PostLogoutRedirectUri = _browser.RedirectUri,

            Scope = options.Scope,
            Browser = _browser,

            // Hands OidcClient's own diagnostics to the same sinks - including the
            // full authorize URL and any protocol error the provider returns.
            LoggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,

            Policy = new Policy
            {
                // OidcClient refuses plain-HTTP discovery by default, which is right for
                // production and wrong for the local lab (http://localhost:8080). Without this
                // the client fails before it ever reaches Keycloak, and the error names the
                // policy rather than the URL — which sends you looking in the wrong place.
                //
                // Only ever relax this for a loopback authority; see the guard below.
                Discovery = new DiscoveryPolicy
                {
                    RequireHttps = !IsLoopback(options.Authority),
                },
            },
        });
    }

    public string RedirectUri => _browser.RedirectUri;

    /// <summary>Interactive sign-in. Opens the system browser.</summary>
    public async Task<StoredTokens> LoginAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.LoginAsync(new LoginRequest(), cancellationToken).ConfigureAwait(false);
        return Persist(result);
    }

    /// <summary>
    /// Re-authenticates at a higher level, in response to an RFC 9470 challenge from the API.
    /// </summary>
    /// <remarks>
    /// <c>acr_values</c> is what asks Keycloak for step-up; the realm's <c>acr.loa.map</c>
    /// translates it to a Level of Authentication, and the browser flow then demands OTP.
    ///
    /// <c>prompt=login</c> is not optional. Without it the existing Keycloak SSO cookie satisfies
    /// the request silently, the user is never challenged, and you have a step-up that steps up
    /// nothing — while appearing to work.
    /// </remarks>
    public async Task<StoredTokens> StepUpAsync(string? acr = null, CancellationToken cancellationToken = default)
    {
        var request = new LoginRequest
        {
            FrontChannelExtraParameters = new Parameters
            {
                { "acr_values", acr ?? _options.StepUpAcr },
                { "prompt", "login" },
            },
        };

        var result = await _client.LoginAsync(request, cancellationToken).ConfigureAwait(false);
        return Persist(result);
    }

    /// <summary>
    /// Returns a usable access token, refreshing it first if it is at or near expiry.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing when there is nothing usable, so the caller can simply
    /// prompt for sign-in. A failed refresh clears the stored tokens: the realm enables
    /// refresh-token reuse detection, so a replayed or revoked token ends the whole session and
    /// retrying would loop forever.
    /// </remarks>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var stored = _store.Load();
        if (stored is null)
        {
            return null;
        }

        if (!stored.NeedsRefresh)
        {
            return stored.AccessToken;
        }

        if (string.IsNullOrEmpty(stored.RefreshToken))
        {
            return null;
        }

        try
        {
            var refreshed = await _client
                .RefreshTokenAsync(stored.RefreshToken, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (refreshed.IsError)
            {
                _store.Clear();
                return null;
            }

            _store.Save(new StoredTokens(
                refreshed.AccessToken,
                // Keycloak rotates refresh tokens; keep the new one or the next refresh fails.
                string.IsNullOrEmpty(refreshed.RefreshToken) ? stored.RefreshToken : refreshed.RefreshToken,
                refreshed.AccessTokenExpiration));

            return refreshed.AccessToken;
        }
        catch (Exception)
        {
            _store.Clear();
            return null;
        }
    }

    /// <summary>Clears local tokens. Does not end the Keycloak SSO session.</summary>
    public void SignOutLocally() => _store.Clear();

    private StoredTokens Persist(LoginResult result)
    {
        if (result.IsError)
        {
            // `invalid_request` here is very often the provider rejecting the
            // redirect URI, so log the one we sent alongside the error.
            _log.LogError(
                "Login failed: {Error} — {Description}. RedirectUri was {RedirectUri}",
                result.Error, result.ErrorDescription ?? "(no description)", _browser.RedirectUri);
            throw new InvalidOperationException(
                $"{result.Error}: {result.ErrorDescription ?? "(no description)"}");
        }

        _log.LogInformation("Signed in; access token expires {Expiry:HH:mm:ss}", result.AccessTokenExpiration);

        var tokens = new StoredTokens(result.AccessToken, result.RefreshToken, result.AccessTokenExpiration);
        _store.Save(tokens);
        return tokens;
    }

    internal static bool IsLoopback(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
