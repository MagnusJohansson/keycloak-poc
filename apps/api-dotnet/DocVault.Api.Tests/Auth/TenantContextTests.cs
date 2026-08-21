using DocVault.Api.Auth;

namespace DocVault.Api.Tests.Auth;

public sealed class TenantContextTests
{
    private static ClaimsPrincipal WithGroups(params string[] groups) =>
        new(new ClaimsIdentity(groups.Select(g => new Claim("groups", g)), "Test"));

    [Fact]
    public void Derives_tenant_and_department_from_the_group_path()
    {
        var context = WithGroups("/acme/engineering").GetTenantContext();

        Assert.Equal("acme", context.Tenant);
        Assert.Equal("engineering", context.Department);
    }

    [Fact]
    public void Handles_a_tenant_level_group_with_no_department()
    {
        var context = WithGroups("/globex").GetTenantContext();

        Assert.Equal("globex", context.Tenant);
        Assert.Null(context.Department);
    }

    [Fact]
    public void Returns_none_when_the_user_has_no_groups()
    {
        Assert.False(WithGroups().GetTenantContext().IsResolved);
    }

    [Fact]
    public void Refuses_to_guess_when_the_user_spans_multiple_tenants()
    {
        // Silently picking the first would be a cross-tenant data leak.
        Assert.Throws<MultipleTenantsException>(
            () => WithGroups("/acme/engineering", "/globex").GetTenantContext());
    }
}
