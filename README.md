# omts-backend

Backend API for **OMTS — Oyster Management and Traceability System** for Trung Nam Aquaculture Cooperative (SEP490_G83).
Sibling repos: [omts-web](https://github.com/thaideptrai218/omts-web) · [omts-mobile](https://github.com/thaideptrai218/omts-mobile)

## Stack

.NET 8 · ASP.NET Core Web API · EF Core · SQL Server · xUnit

## Structure (Clean Architecture)

```
src/
  OMTS.Domain/          Entities, value objects, business rules. No dependencies.
  OMTS.Application/     Use cases and ports (interfaces). Depends on Domain.
  OMTS.Infrastructure/  EF Core DbContext, external adapters (e.g. National Traceability Portal). Depends on Application.
  OMTS.Api/             HTTP endpoints, DI composition root. Depends on Application + Infrastructure.
tests/
  OMTS.Domain.UnitTests/  OMTS.Application.UnitTests/  OMTS.Api.IntegrationTests/
```

Dependencies point inward only; tests in `tests/` guard this rule.

## Getting started

Prerequisites: .NET SDK 8.0 (pinned by `global.json`), SQL Server (LocalDB / Docker / Azure SQL).

```bash
dotnet restore
# Connection string is kept out of git via user-secrets:
dotnet user-secrets set "ConnectionStrings:OmtsDb" "Server=localhost;Database=OMTS;User Id=sa;Password=<pwd>;TrustServerCertificate=True" --project src/OMTS.Api
dotnet run --project src/OMTS.Api        # Swagger at /swagger, health at /health
dotnet test                               # all tests
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — branches, Jira keys, commit/PR rules, releases.
