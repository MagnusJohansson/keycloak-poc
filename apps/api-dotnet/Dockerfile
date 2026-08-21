# Multi-stage: the runtime image carries no SDK and no source.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore as its own layer so a source-only change does not re-download packages.
COPY DocVault.slnx ./
COPY DocVault.Api/DocVault.Api.csproj DocVault.Api/
COPY DocVault.Worker/DocVault.Worker.csproj DocVault.Worker/
RUN dotnet restore DocVault.Api/DocVault.Api.csproj

COPY DocVault.Api/ DocVault.Api/
RUN dotnet publish DocVault.Api/DocVault.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Run as a non-root user. The aspnet image ships one; using it means a container
# escape does not start with root.
USER $APP_UID

# Matches the Container App's target_port and the liveness probe in 30-azure.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "DocVault.Api.dll"]
