using System.Diagnostics;
using System.Net;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;

namespace DocVault.Desktop.Auth;

/// <summary>
/// Runs the authorization request in the user's real browser and catches the redirect on a
/// loopback HTTP listener, per RFC 8252 ("OAuth 2.0 for Native Apps").
/// </summary>
/// <remarks>
/// <para>
/// The alternative — hosting a WebView2 inside the app — is what this deliberately avoids. An
/// embedded browser is controlled by the application, so it <em>can</em> read the password as the
/// user types it; it cannot share the system single-sign-on session; and it hides the real
/// address bar, so the user cannot check who is asking for their credentials. Identity providers
/// increasingly reject embedded-browser traffic for exactly those reasons.
/// </para>
/// <para>
/// This is the same approach as the Electron client in <c>apps/desktop-electron</c>, which is the
/// point: the pattern is a property of native apps, not of any one UI framework.
/// </para>
/// </remarks>
public sealed class LoopbackBrowser : IBrowser
{
    private readonly int _timeoutSeconds;

    public LoopbackBrowser(int timeoutSeconds = 300) => _timeoutSeconds = timeoutSeconds;

    /// <summary>The redirect URI this browser will listen on, fixed for the instance's lifetime.</summary>
    public string RedirectUri { get; } = $"http://127.0.0.1:{GetFreePort()}/callback";

    public async Task<BrowserResult> InvokeAsync(
        BrowserOptions options,
        CancellationToken cancellationToken = default)
    {
        using var listener = new HttpListener();

        // The trailing slash is required; HttpListener throws without it.
        listener.Prefixes.Add(RedirectUri.EndsWith('/') ? RedirectUri : RedirectUri + "/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            return Failure(BrowserResultType.UnknownError, $"Could not listen on {RedirectUri}: {ex.Message}");
        }

        OpenSystemBrowser(options.StartUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        HttpListenerContext context;
        try
        {
            // GetContextAsync ignores cancellation tokens, so race it against the timeout and
            // stop the listener to unblock it. Without this the app hangs forever when the user
            // simply closes the browser tab.
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token))
                .ConfigureAwait(false);

            if (completed != contextTask)
            {
                return Failure(BrowserResultType.Timeout, "Timed out waiting for the browser redirect.");
            }

            context = await contextTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(BrowserResultType.UnknownError, ex.Message);
        }

        // The authorization response arrives as the query string. OidcClient parses and
        // validates it (including the state and PKCE verifier) — this only transports it.
        var response = context.Request.Url?.Query ?? string.Empty;

        await WriteClosingPageAsync(context.Response).ConfigureAwait(false);

        return new BrowserResult
        {
            ResultType = BrowserResultType.Success,
            Response = response,
        };
    }

    /// <summary>
    /// Asks the OS to open the URL, which reaches the user's default browser.
    /// </summary>
    /// <remarks>
    /// <c>UseShellExecute = true</c> is what makes this the *system* browser rather than an
    /// attempt to execute the URL as a program. It is also required on .NET Core and later,
    /// where the default is <c>false</c>.
    /// </remarks>
    private static void OpenSystemBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>
    /// Asks the OS for an unused TCP port by binding port 0.
    /// </summary>
    /// <remarks>
    /// Deliberately not a fixed port. A hardcoded port collides with whatever else the user is
    /// running, and cannot support two instances of the app. Keycloak permits this because the
    /// <c>docvault-winui</c> client registers <c>http://127.0.0.1:*/callback</c> — a wildcard
    /// port is explicitly allowed for native apps, unlike a wildcard host.
    ///
    /// 127.0.0.1 rather than "localhost": localhost can resolve to IPv6 ::1, and the listener
    /// would then never see the callback.
    /// </remarks>
    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WriteClosingPageAsync(HttpListenerResponse response)
    {
        const string html = """
            <!doctype html><html><head><meta charset="utf-8"><title>Signed in</title></head>
            <body style="font-family:system-ui;padding:2rem">
            <h2>Signed in to DocVault</h2><p>You can close this tab and return to the app.</p>
            </body></html>
            """;

        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private static BrowserResult Failure(BrowserResultType type, string error) =>
        new() { ResultType = type, Error = error };
}
