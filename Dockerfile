# OMTS API image: the .NET 8 SDK publishes OMTS.Api, the ASP.NET runtime image runs it as a non-root user on
# plain HTTP port 8080. Used by docker-compose.yml (local environment) and built by CI without being pushed.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Restore from the project files only, so this layer is reused while only code changes. global.json pins the
# same SDK feature band as CI.
COPY global.json Directory.Build.props ./
COPY src/OMTS.Domain/OMTS.Domain.csproj src/OMTS.Domain/
COPY src/OMTS.Application/OMTS.Application.csproj src/OMTS.Application/
COPY src/OMTS.Infrastructure/OMTS.Infrastructure.csproj src/OMTS.Infrastructure/
COPY src/OMTS.Api/OMTS.Api.csproj src/OMTS.Api/
RUN dotnet restore src/OMTS.Api/OMTS.Api.csproj

COPY src/ src/
RUN dotnet publish src/OMTS.Api/OMTS.Api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Local file storage (Storage:Provider = Local) writes under App_Data; the folder belongs to the app user so a
# named volume mounted there starts with the right owner.
RUN mkdir -p /app/App_Data/files && chown -R "$APP_UID" /app/App_Data

# APP_UID is the non-root "app" user shipped with the .NET 8 images.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OMTS.Api.dll"]
