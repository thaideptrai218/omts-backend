# OMTS Backend

ASP.NET Core 10 backend foundation with verified API hosting, container delivery, and pull request conventions.

This repository is a platform foundation. Product endpoints, identity provider, persistence, production infrastructure, and service-level objectives must be specified before a business service can be released.

## Development

Install the .NET SDK selected by `global.json`, then run:

```sh
dotnet restore --locked-mode
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release
dotnet format --verify-no-changes --no-restore
dotnet run --project src/Omts.Api
```

See [architecture](docs/system-architecture.md), [code standards](docs/code-standards.md), [Git workflow](CONTRIBUTING.md), and [deployment](docs/deployment-guide.md).

## Ownership

Private repository. No open-source license is granted. Report security issues using the process in [SECURITY.md](SECURITY.md).
