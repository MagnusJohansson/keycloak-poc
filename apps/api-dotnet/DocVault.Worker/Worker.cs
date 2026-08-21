using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DocVault.Worker;

/// <summary>
/// A daemon with no user: use-case 4, machine-to-machine authentication.
/// </summary>
/// <remarks>
/// <para>
/// There is no browser, no redirect and no consent here — just the
/// <c>client_credentials</c> grant. Keycloak issues a token for the client's own
/// service account, which holds roles exactly as a human would (this one has
/// <c>doc.reader</c> and nothing else; see
/// <c>infra/terraform/20-realm/authz-documents.tf</c>).
/// </para>
/// <para>
/// The key property: no long-lived API key is embedded anywhere. The client
/// secret is exchanged for a short-lived token on demand, so a leaked token
/// expires in minutes and the secret itself can be rotated centrally.
/// </para>
/// </remarks>
public sealed class Worker(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<Worker> logger) : BackgroundService
{
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await IndexDocumentsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Indexing pass failed; will retry.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task IndexDocumentsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("api");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken));

        var response = await client.GetAsync("/documents", cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // Expected, and worth seeing: the service account holds doc.reader but
            // belongs to no tenant group, so the API cannot scope a query for it.
            // Machine identities need to be modelled just as deliberately as human
            // ones — a role alone is not an authorization.
            logger.LogWarning(
                "403 from /documents. The worker's service account has no tenant group, " +
                "so there is nothing for it to read. Add it to a group in Terraform to fix.");
            return;
        }

        response.EnsureSuccessStatusCode();
        logger.LogInformation("Indexed documents: {Payload}", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>Fetches (and caches) a token via the client_credentials grant.</summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // Re-use until it is nearly expired. Requesting a fresh token per call
        // works but hammers Keycloak needlessly; the 60s margin absorbs clock
        // drift and request latency.
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddSeconds(-60))
        {
            return _cachedToken;
        }

        var authority = configuration["Keycloak:Authority"]
            ?? throw new InvalidOperationException("Keycloak:Authority must be configured.");
        var clientId = configuration["Keycloak:ClientId"] ?? "docvault-worker";
        var clientSecret = configuration["Keycloak:ClientSecret"]
            ?? throw new InvalidOperationException(
                "Keycloak:ClientSecret must be configured. Get it with `make show-secrets`, " +
                "and in production read it from Key Vault rather than configuration.");

        using var tokenClient = httpClientFactory.CreateClient();
        using var response = await tokenClient.PostAsync(
            $"{authority}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        _cachedToken = document.RootElement.GetProperty("access_token").GetString()!;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(document.RootElement.GetProperty("expires_in").GetInt32());

        logger.LogInformation("Obtained a service-account token, valid until {Expiry:HH:mm:ss}.", _tokenExpiry);
        return _cachedToken;
    }
}
