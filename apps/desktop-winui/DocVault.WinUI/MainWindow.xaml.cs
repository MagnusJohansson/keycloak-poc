using System.Text.Json;
using DocVault.Desktop.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DocVault.WinUI;

/// <summary>
/// The UI shell. Deliberately thin: every OIDC decision lives in DocVault.Desktop.Auth, which is
/// a plain net10.0 library so it can be unit-tested on any operating system.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly KeycloakDesktopClient _auth;
    private readonly DocVaultApiClient _api;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainWindow> _log;

    public MainWindow()
    {
        InitializeComponent();

        // Goes to %LOCALAPPDATA%\DocVault\logs, the Visual Studio Output window,
        // and stdout. A GUI app has no console, so the file is the one that survives.
        _loggerFactory = DesktopLogging.Create();
        _log = _loggerFactory.CreateLogger<MainWindow>();

        // appsettings.json next to the executable, overridable with DOCVAULT_* environment
        // variables. Loading and validation live in DocVault.Desktop.Auth so they can be
        // unit-tested without Windows.
        var settings = DesktopSettings.Load();

        // DPAPI keeps tokens encrypted at rest under the current Windows account, so closing the
        // app does not mean signing in again.
        _auth = new KeycloakDesktopClient(settings.ToAuthOptions(), new DpapiTokenStore(), _loggerFactory);
        _api = new DocVaultApiClient(new HttpClient { BaseAddress = new Uri(settings.ApiBaseUrl) });

        _log.LogInformation("ApiBaseUrl  {ApiBaseUrl}", settings.ApiBaseUrl);

        // Tell the user where the log is, so a bug report can include it.
        Report(InfoBarSeverity.Informational, $"Log: {DesktopLogging.CurrentLogFile}");
    }

    private async void OnSignIn(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            var tokens = await _auth.LoginAsync();
            ShowIdentity(tokens.AccessToken);
            Report(InfoBarSeverity.Success, "Signed in.");
        });

    private async void OnLoadDocuments(object sender, RoutedEventArgs e) =>
        await RunAsync(() => LoadAsync("/documents"));

    private async void OnLoadClassified(object sender, RoutedEventArgs e) =>
        await RunAsync(() => LoadAsync("/documents/classified"));

    private async void OnSignOut(object sender, RoutedEventArgs e) =>
        await RunAsync(() =>
        {
            _auth.SignOutLocally();
            Documents.ItemsSource = null;
            Claims.Text = string.Empty;
            Identity.Text = string.Empty;

            // Local only: the Keycloak SSO cookie in the browser is untouched, so the next
            // sign-in may not prompt. End the IdP session too if that matters to you.
            Report(InfoBarSeverity.Informational, "Signed out locally. The browser session is still active.");
            return Task.CompletedTask;
        });

    private async Task LoadAsync(string path)
    {
        var token = await _auth.GetAccessTokenAsync();
        if (token is null)
        {
            Report(InfoBarSeverity.Warning, "Not signed in.");
            return;
        }

        try
        {
            var payload = await _api.GetAsync(token, path);
            ShowIdentity(token);

            var titles = payload.TryGetProperty("documents", out var documents)
                ? documents.EnumerateArray().Select(d => d.GetProperty("title").GetString()).ToList()
                : [];

            Documents.ItemsSource = titles;
            Report(InfoBarSeverity.Success, $"{titles.Count} document(s).");
        }
        catch (StepUpRequiredException ex)
        {
            // The API told us exactly how to recover (RFC 9470), so offer it rather than
            // showing a dead-end error. prompt=login is applied inside StepUpAsync.
            Report(InfoBarSeverity.Warning, $"Additional verification required — re-authenticating at '{ex.RequiredAcr}'.");

            var tokens = await _auth.StepUpAsync(ex.RequiredAcr);
            ShowIdentity(tokens.AccessToken);

            var payload = await _api.GetAsync(tokens.AccessToken!, path);
            Documents.ItemsSource = payload.TryGetProperty("documents", out var documents)
                ? documents.EnumerateArray().Select(d => d.GetProperty("title").GetString()).ToList()
                : [];

            Report(InfoBarSeverity.Success, "Access granted after step-up.");
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            // A plain 403: the caller lacks the role. Re-authenticating would not help, so do
            // not send them back through sign-in - that is a loop which can never succeed.
            Report(InfoBarSeverity.Error, "Forbidden — your account lacks the required role.");
        }
    }

    private void ShowIdentity(string? accessToken)
    {
        var payload = TokenClaims.DecodePayload(accessToken);
        var roles = TokenClaims.Roles(payload);

        Identity.Text = payload is null
            ? string.Empty
            : $"{TokenClaims.Username(payload)}  ·  tenant {TokenClaims.Tenant(payload) ?? "(none)"}"
              + $"  ·  acr {TokenClaims.Acr(payload) ?? "(none)"}"
              + $"  ·  roles {(roles.Count > 0 ? string.Join(", ", roles) : "(none)")}";

        Claims.Text = TokenClaims.Pretty(payload);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Action failed");
            Report(InfoBarSeverity.Error, $"{ex.Message}  —  see {DesktopLogging.CurrentLogFile}");
        }
    }

    private void Report(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }
}
