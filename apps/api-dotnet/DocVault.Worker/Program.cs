using DocVault.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Keycloak:ApiBaseUrl"] ?? "http://localhost:5001");
});

builder.Services.AddHostedService<Worker>();

builder.Build().Run();
