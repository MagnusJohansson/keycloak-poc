using DocVault.Desktop.Auth;

namespace DocVault.Desktop.Auth.Tests;

public class DesktopSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docvault-settings").FullName;
    private readonly List<string> _envVars = [];

    private void WriteSettings(string json) =>
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), json);

    private void SetEnv(string name, string? value)
    {
        _envVars.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public void Defaults_to_the_local_lab_when_nothing_is_configured()
    {
        var settings = DesktopSettings.Load(_dir);

        Assert.Equal("http://localhost:8080/realms/docvault", settings.Authority);
        Assert.Equal("http://localhost:5001", settings.ApiBaseUrl);
        Assert.Equal("docvault-winui", settings.ClientId);
    }

    [Fact]
    public void Reads_appsettings_json()
    {
        WriteSettings("""
            {"Authority":"https://kc.example.com/realms/docvault",
             "ApiBaseUrl":"https://api.example.com"}
            """);

        var settings = DesktopSettings.Load(_dir);

        Assert.Equal("https://kc.example.com/realms/docvault", settings.Authority);
        Assert.Equal("https://api.example.com", settings.ApiBaseUrl);
        Assert.Equal("docvault-winui", settings.ClientId);   // untouched keys keep their default
    }

    [Fact]
    public void Environment_variables_override_the_file()
    {
        // So CI, or a second instance pointed at another realm, can override without
        // editing a file that ships with the app.
        WriteSettings("""{"Authority":"https://from-file/realms/docvault"}""");
        SetEnv("DOCVAULT_AUTHORITY", "https://from-env/realms/docvault");

        Assert.Equal("https://from-env/realms/docvault", DesktopSettings.Load(_dir).Authority);
    }

    [Fact]
    public void Rejects_an_authority_that_is_not_a_realm_issuer()
    {
        // Pointing at the host root is a common slip; discovery then 404s in a way
        // that reads like the server is down.
        WriteSettings("""{"Authority":"https://kc.example.com"}""");

        var ex = Assert.Throws<InvalidOperationException>(() => DesktopSettings.Load(_dir));
        Assert.Contains("/realms/", ex.Message);
    }

    [Theory]
    [InlineData("localhost:8080/realms/docvault")]  // Uri parses this as scheme "localhost"
    [InlineData("/realms/docvault")]
    [InlineData("ftp://kc.example.com/realms/docvault")]
    public void Rejects_an_authority_that_is_not_an_absolute_http_url(string authority)
    {
        WriteSettings($$"""{"Authority":"{{authority}}"}""");

        Assert.Throws<InvalidOperationException>(() => DesktopSettings.Load(_dir));
    }

    [Fact]
    public void Rejects_an_empty_client_id()
    {
        WriteSettings("""{"ClientId":""}""");

        Assert.Throws<InvalidOperationException>(() => DesktopSettings.Load(_dir));
    }

    [Fact]
    public void Projects_onto_the_options_the_oidc_client_needs()
    {
        WriteSettings("""{"Authority":"https://kc.example.com/realms/docvault","StepUpAcr":"gold"}""");

        var options = DesktopSettings.Load(_dir).ToAuthOptions();

        Assert.Equal("https://kc.example.com/realms/docvault", options.Authority);
        Assert.Equal("docvault-winui", options.ClientId);
        Assert.Equal("gold", options.StepUpAcr);
    }

    public void Dispose()
    {
        foreach (var name in _envVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Directory.Delete(_dir, recursive: true);
    }
}
