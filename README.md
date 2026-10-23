# omts-backend

Backend API for **OMTS — Oyster Management and Traceability System** for Trung Nam Aquaculture Cooperative (SEP490_G83).
Sibling repos: [omts-web](https://github.com/thaideptrai218/omts-web) · [omts-mobile](https://github.com/thaideptrai218/omts-mobile)

## Stack

.NET 8 · ASP.NET Core Web API · EF Core · SQL Server · xUnit

## Structure (Clean Architecture)

```
src/
  OMTS.Domain/                 No dependencies.
    Common/                    Entity, AuditableEntity, IHasStatus
    Constants/                 StatusIds, StatusGroups (fixed Status catalog)
    Enums/
    Entities/<Group>/          52 SRS entities by group: Identity, Farming, Cultivation, Sales,
                               Traceability, AfterSales, Finance, Labor, Platform
  OMTS.Application/            Use cases and ports. Depends on Domain only (no EF Core).
    Common/Interfaces/         Ports: Persistence/ (IUnitOfWork, I*Repository), Identity/, ICurrentUser
    Common/Models/             Types the ports exchange (AccessToken, EffectiveGrant, ...)
    Common/Constants/
    Features/<Feature>/        Services of a feature, with Dtos/ (API contracts) and Models/
  OMTS.Infrastructure/         Implements the ports. Depends on Application.
    Identity/                  JWT tokens, password hashing
    Persistence/               OmtsDbContext, UnitOfWork
      Configurations/<Group>/  One <Entity>Configuration per entity, same groups as Domain
      Repositories/  Interceptors/  Seed/  Migrations/
  OMTS.Api/                    HTTP endpoints, DI composition root.
    Controllers/  Filters/  Extensions/  Services/  Common/
tests/
  OMTS.Domain.UnitTests/  OMTS.Application.UnitTests/
  OMTS.Api.IntegrationTests/   Infrastructure/ (test host, test-only endpoints), one folder per feature
```

Dependencies point inward only; tests in `tests/` guard this rule. Conventions: one type per file,
namespace = folder path, interfaces (ports) live under `Application/Common/Interfaces`.

## Getting started

Prerequisites: .NET SDK 8.0 (pinned by `global.json`), SQL Server (LocalDB / Docker / Azure SQL).

```bash
dotnet restore
# Connection string is kept out of git via user-secrets:
dotnet user-secrets set "ConnectionStrings:OmtsDb" "Server=localhost;Database=OMTS;User Id=sa;Password=<pwd>;TrustServerCertificate=True" --project src/OMTS.Api
# JWT signing key (random, at least 32 bytes), e.g. from: openssl rand -base64 64
dotnet user-secrets set "Jwt:SigningKey" "<random key>" --project src/OMTS.Api
dotnet run --project src/OMTS.Api        # Swagger at /swagger, health at /health
dotnet test                               # all tests
```

API integration tests use an in-memory database. Tests marked `[SqlServerFact]` (migrations, constraints,
transactions) need a real SQL Server and are skipped unless `OMTS_TEST_SQLSERVER` holds a server connection
string; each test creates and drops its own database. CI starts a SQL Server container for them.

```bash
OMTS_TEST_SQLSERVER="Server=localhost;User Id=sa;Password=<pwd>;TrustServerCertificate=True" dotnet test
```

### Authentication

`POST /api/auth/login` (username, verified phone or verified email) returns a 15-minute access token and a
refresh token (7 days web / 30 days mobile, rotated on every `POST /api/auth/refresh`); `POST /api/auth/logout`
revokes the session. `GET /api/me` and `GET /api/me/permissions` return the caller and their effective
zone-scoped permissions. Errors are RFC 7807 problem details whose `code` is the SRS message code
(`MSG12`, `MSG69`…) and whose `detail` is the SRS English text.

### Database (EF Core code-first)

Entities live in `OMTS.Domain`; the DbContext, configurations and migrations live in `OMTS.Infrastructure/Persistence`.
`dotnet ef` uses `OMTS.Api` as the startup project, so it reads the same `OmtsDb` user-secret as the API — set it once per machine (see above).

```bash
dotnet tool install --global dotnet-ef   # once
# create/update your local database from the migrations
dotnet ef database update --project src/OMTS.Infrastructure --startup-project src/OMTS.Api
# after changing entities or configurations
dotnet ef migrations add <Name> --project src/OMTS.Infrastructure --startup-project src/OMTS.Api --output-dir Persistence/Migrations
```

### External services (development)

Email, SMS, push and file storage are adapters picked by configuration, so switching provider needs no code change.
In Development the fakes keep what they "send" in memory; read it at `GET /api/dev/sms?phoneNumber=`,
`GET /api/dev/email?to=`, `GET /api/dev/push?accountId=` (clear with `DELETE`). These endpoints answer 404 outside
Development; they are anonymous, so never expose a Development environment publicly. Local storage writes under `src/OMTS.Api/App_Data/files` (git-ignored); for Azurite set
`Storage:Provider=AzureBlob` and `Storage:AzureBlob:ConnectionString=UseDevelopmentStorage=true`.

## Deployment

There is no System Administrator inside the cooperative app: the cooperative and its first Cooperative Owner are
created by a one-time command run on the server by the deployment operator (not reachable over HTTP).

1. Set the secrets (environment variables or the hosting secret store; `__` replaces `:` in variable names):
   `ConnectionStrings:OmtsDb`, `Jwt:SigningKey`, the provider keys below, and the bootstrap keys
   `Bootstrap:Cooperative:FullName`, `Bootstrap:Owner:DisplayName`, `Bootstrap:Owner:Username`,
   `Bootstrap:Owner:PhoneNumber`, `Bootstrap:Owner:Email` (optional), `Bootstrap:Owner:TemporaryPassword`.
2. Run `dotnet OMTS.Api.dll seed --bootstrap` at handover time: it applies migrations, checks the status catalog, creates
   the default roles (Zone Manager, Accountant) and, if no cooperative exists, the cooperative (Preliminary) and the
   Owner with the temporary password. The temporary password expires after 24 hours and must be changed at first login.
   Running it again creates nothing that already exists ("already initialized", exit code 0); only a default role
   whose name no longer exists (for example after a rename) is created again. Missing secrets give exit code 1 and the
   names of the missing keys. `--demo` also creates the demo zones `DEMO-01`/`DEMO-02` (Development only).
3. Start the API and check `GET /health/ready`: 200 when the database, migrations, status catalog, default roles,
   cooperative and an active Owner are all in place, else 503 listing the failing checks. This is a post-deployment
   verification, **not** a platform probe: it depends on data the Owner can change (renaming a default role or locking the
   only Owner turns it 503). Point App Service / Kubernetes readiness and load-balancer probes at `GET /health/db`
   (database reachable and migrated). `GET /health` is liveness only.

| Area | Keys |
|---|---|
| Email (`Email:Provider` = `Smtp` or `Fake`; `Email:Smtp:Security` = `StartTls` (587), `SslOnConnect` (465) or `None`, which is refused outside Development) | `Email:Smtp:Host`, `Port`, `Security`, `Username`, `Password`, `FromAddress`, `FromName`, `TimeoutSeconds`, `Email:Fake:FailFor` |
| SMS (`Sms:Provider` = `None`, the default: nothing is sent and phone verification cannot complete; `Fake` keeps messages in the dev inbox and is set in Development only) | `Sms:Fake:FailFor` (test numbers that fail) |
| Push (`Push:Provider` = `None` by default, `Fake` in Development) | `Push:Fake:FailFor` |
| File storage (`Storage:Provider` = `Local` or `AzureBlob`; `Storage:Local:SigningKey` is required outside Development) | `Storage:Local:RootPath`, `Storage:Local:SigningKey`, `Storage:AzureBlob:ConnectionString`, `Storage:AzureBlob:Container` |
| Bootstrap (see Deployment) | `Bootstrap:Cooperative:FullName`, `Bootstrap:Owner:*`, `Bootstrap:SeedDemoData` (same as `--demo`, Development only) |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — branches, Jira keys, commit/PR rules, releases.
