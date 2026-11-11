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
    Entities/<Group>/          Entities by group: Identity, Farming, Cultivation, Harvest, Purchasing, Sales,
                               Labor, CashBook, Platform
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

## Run with Docker

Requirements: Docker Desktop (or Docker Engine with Compose v2). No .NET SDK, SQL Server or user-secrets are needed.
Ports 8080 (API) and 1433 (SQL Server, this PC only) must be free.

```bash
cp .env.example .env            # cmd.exe: copy .env.example .env. Local-only values; .env is git-ignored, never commit it
docker compose up -d --build    # first run builds the image and seeds the database (a few minutes)
```

Fill in the secrets in `.env` first: `MSSQL_SA_PASSWORD`, `JWT_SIGNING_KEY`, `STORAGE_SIGNING_KEY`, `OWNER_TEMPORARY_PASSWORD`, `REDIS_PASSWORD`.
Compose stops with "set it in .env" when one is missing.

Compose starts SQL Server and Redis, runs `seed --bootstrap --demo` once (migrations, the cooperative and the demo data), then starts the API in Development. The seed is idempotent: it runs on every `docker compose up` and creates nothing twice. Demo data details: [operations reference](docs/operations-reference.md#demo-data-docker).

| What | Where |
|---|---|
| API | `http://localhost:8080` |
| Swagger | `http://localhost:8080/swagger` |
| Liveness | `http://localhost:8080/health` |
| Database health | `http://localhost:8080/health/db` |
| Deployment check (migrations, roles, cooperative, active Owner) | `http://localhost:8080/health/ready` |
| SQL Server (this PC only) | `localhost,1433`, user `sa`, password `MSSQL_SA_PASSWORD` from `.env` |
| Redis (containers only) | Not published. See [session revocation store](docs/api-behaviour-reference.md#session-revocation-store-redis) |

### Demo logins

Every account below uses the password `Demo@1234`. It exists only in this local demo data; a shared server uses a secret of its own (see [Demo on a shared server](#demo-on-a-shared-server)).

| Username | Role | Zone | What happens at login |
|---|---|---|---|
| `owner` | Cooperative Owner | all | Normal login |
| `quanlyvung1` | Zone Manager | DEMO-01 | Normal login |
| `quanlyvung2` | Zone Manager | DEMO-02 | Normal login |
| `ketoan` | Accountant | all zones | Normal login |
| `thuonglai1` | Trader (Active) | – | Normal login |
| `thuonglai2` | Trader (Locked) | – | Refused: the account is locked |
| `nhanvien.doimk` | Zone Manager | DEMO-01 | Logged in with `mustChangePassword = true` (change it first) |
| `nhanvien.chuaxacthuc` | Zone Manager | DEMO-02 | Logged in with `phoneVerificationRequired = true` (verify the phone first) |
| `nhanvien.hethan` | Zone Manager | DEMO-01, pair revoked | Refused: no Effective zone–role pair (MSG-ACCOUNT-07) |

Login accepts a phone number or a username with the password (the web form too). A changed password stays changed; to get every demo login back, reset the environment with `docker compose down -v` (this deletes the database and stored files).

**Verification codes.** SMS, email and push use the fake providers: nothing leaves the machine. What would have been
sent stays in memory until the API restarts. Read it in the dev inbox, also listed in Swagger:
- `GET /api/dev/sms?phoneNumber=`
- `GET /api/dev/email?to=`
- `GET /api/dev/push?accountId=`

`docker compose logs -f api` shows each fake message's id and masked recipient, never the code.

For example, `nhanvien.chuaxacthuc` (phone `0900000015`) logs in with `phoneVerificationRequired = true`, and its
business calls answer MSG-ACCOUNT-04 until the phone is verified. Logging in sends no code; with that login's access token:
1. `POST /api/me/phone-verification` with `{}` sends the code.
2. `GET /api/dev/sms?phoneNumber=0900000015` shows it.
3. `POST /api/verifications/{id}/verify` with `{ "code": "…" }` returns a verification token.
4. `POST /api/me/phone-verification/confirm` with `{ "verificationToken": "…" }` completes it.

The dev inbox is anonymous, so anyone on the same network can read the codes. Run this environment only on a network
you trust, never on public Wi-Fi.

**Phone on the same Wi-Fi.** The API listens on every interface of the PC. Find the PC's LAN IP (`ipconfig` on Windows,
`ipconfig getifaddr en0` on macOS) and point the mobile app at `http://<LAN IP>:8080`. If the phone cannot connect, allow
inbound TCP 8080 in the PC's firewall. The Android emulator reaches the PC at `http://10.0.2.2:8080`. File links (license
documents, raft drawings, supplier certificates) are relative, signed paths such as `/api/files/local/...?expires=…&sig=…`.
Prefix them with the API base URL.

**Web app and Zalo Mini App.** In Development `Cors:AllowedOrigins` allows `http://localhost:3000` (the Next.js dev
server) and `https://h5.zdn.vn` (Zalo serves every `zmp deploy` version of the Mini App, Development, Testing and
published, from that origin). For another origin, add a line such as `Cors__AllowedOrigins__2: http://192.168.1.10:3000` under
`environment` in `docker-compose.yml`, then run `docker compose up -d`.

**Update, reset, stop.**
- `docker compose up -d --build`: after a pull. It rebuilds the image, and the seed applies new migrations. If `.env` has no
  `REDIS_PASSWORD=` line, copy it from `.env.example`; compose stops with "set it in .env" otherwise.
- `docker compose down -v`: deletes the database and the stored files. The next `up` seeds a fresh copy.
- `docker compose down`: stops everything and keeps the data.

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

## Deployment

Summary; the full step list, upgrade notes and configuration keys are in the [operations reference](docs/operations-reference.md#deployment).

1. Set the secrets (environment variables or the hosting secret store; `__` replaces `:` in names): `ConnectionStrings:OmtsDb`, `Jwt:SigningKey`, `Provenance:PublicBaseUrl`, the provider keys and the `Bootstrap:*` keys.
2. Run `dotnet OMTS.Api.dll seed --bootstrap` once at handover. It applies migrations, creates the fixed roles, the cooperative and the first Owner. Running it again creates nothing that already exists.
3. Start the API and check `GET /health/ready` (200 when the deployment is complete, else 503 listing the failing checks). Point load-balancer and platform probes at `GET /health/db`, not at `/health/ready`.

Owner password recovery: `dotnet OMTS.Api.dll seed --reset-owner-password` (sets `Bootstrap:Owner:TemporaryPassword` as a new temporary password; see the operations reference).

## Demo on a shared server

Do not use `docker-compose.yml` as a shared server: it runs in Development and listens on all interfaces, and its dev inbox shows verification codes to anyone on the network.
A shared server runs in **Staging**, with an empty database, `seed --bootstrap --demo`, `Demo__AllowOutsideDevelopment=true` and a `Demo__Password` secret. Full setup and rules: [operations reference](docs/operations-reference.md#demo-on-a-shared-server).

## Documentation

| File | Content |
|---|---|
| [docs/api-behaviour-reference.md](docs/api-behaviour-reference.md) | Detailed rules: authentication and sessions, roles, notifications, provenance, sales, claims, workforce, audit, VietQR, background jobs |
| [docs/operations-reference.md](docs/operations-reference.md) | Demo data, external-service adapters, deployment, configuration keys, shared server |
| [docs/SRS_DEVIATIONS.md](docs/SRS_DEVIATIONS.md) | Backend-side differences from SRS v8 (summary; the master list is in the web repository) |
| [docs/sds/](docs/sds/) | Detailed design (SDS) of the modules; a PR that changes a documented module updates its SDS part |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — branches, Jira keys, commit/PR rules, releases.

## Troubleshooting

| Symptom | What to check |
|---|---|
| Compose stops with "set it in .env" | A required value is missing in `.env`. Copy it from `.env.example`. |
| Port 1433 already in use | A local SQL Server default instance is listening. Stop it, then run `docker compose up -d` again. |
| The API does not start or `seed` exits with code 1 | Read the message: it names the missing key, never the value. Missing secrets give exit code 1. |
| `seed` refuses to run on a database with accounts | Demo seeding only runs on an empty or self-seeded database. Start from an empty database (`docker compose down -v` in local Docker). |
| `/health/ready` returns 503 | The body lists the failing checks (database, migrations, status catalog, roles, cooperative, active Owner). |
| Verification codes do not arrive | Local Docker uses the fake providers: read the codes at `GET /api/dev/sms?phoneNumber=` (or `/email`, `/push`). Nothing is sent. |
