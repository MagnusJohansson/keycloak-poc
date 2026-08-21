using DocVault.Api.Auth;
using DocVault.Api.Domain;
using DocVault.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDocVaultAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddDocVaultAuthorization();

builder.Services.AddSingleton<IDocumentStore, InMemoryDocumentStore>();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// The SPAs run on a different origin, so the browser will preflight every
// authenticated request. `AllowCredentials` is deliberately absent: tokens travel in
// the Authorization header, not in cookies, so cookie-credentialed CORS is not needed
// and enabling it would widen the attack surface for nothing.
const string SpaCors = "spa";
builder.Services.AddCors(options => options.AddPolicy(SpaCors, policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .WithHeaders("Authorization", "Content-Type")
    .WithMethods("GET", "POST", "PUT", "DELETE")

    // WWW-Authenticate is NOT one of the CORS-safelisted response headers, so a
    // cross-origin SPA cannot read it unless it is explicitly exposed. Without
    // this line the step-up challenge (RFC 9470) is invisible to the browser:
    // the API correctly returns 403 + acr_values="silver", the SPA sees only a
    // bare 403, and the user hits a dead end with no way to recover.
    .WithExposedHeaders("WWW-Authenticate")));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(SpaCors);

// Order matters and is a classic source of 401s: authentication must populate the
// principal before authorization can evaluate it.
app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityEndpoints();
app.MapDocumentEndpoints();
app.MapAnalyticsEndpoints();
app.MapAdminEndpoints();

app.Run();

// Exposed so DocVault.Api.Tests can drive the real pipeline through
// WebApplicationFactory<Program> rather than re-creating a fake one.
public partial class Program;
