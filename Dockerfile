FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/ ./src/
RUN dotnet restore src/Omts.Api/Omts.Api.csproj --locked-mode \
    -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low \
    -p:TreatWarningsAsErrors=true
RUN dotnet publish src/Omts.Api/Omts.Api.csproj --no-restore \
    --configuration Release --output /app/publish -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Omts.Api.dll"]
