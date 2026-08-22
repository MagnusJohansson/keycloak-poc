using DocVault.Desktop.Auth;

namespace DocVault.Desktop.Auth.Tests;

/// <summary>
/// Plain-HTTP OIDC discovery is only tolerable against loopback. Relaxing it for a remote host
/// would let an attacker serve their own signing keys and mint tokens this client would accept.
/// </summary>
public class AuthorityPolicyTests
{
    [Theory]
    [InlineData("http://localhost:8080/realms/docvault")]
    [InlineData("http://127.0.0.1:8080/realms/docvault")]
    public void Loopback_authorities_are_recognised(string authority)
    {
        Assert.True(KeycloakDesktopClient.IsLoopback(authority));
    }

    [Theory]
    [InlineData("https://ca-keycloak.swedencentral.azurecontainerapps.io/realms/docvault")]
    [InlineData("http://keycloak.internal.example.com/realms/docvault")]
    [InlineData("not-a-uri")]
    public void Remote_authorities_are_not(string authority)
    {
        Assert.False(KeycloakDesktopClient.IsLoopback(authority));
    }
}
