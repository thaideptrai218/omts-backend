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

## Run with Docker (web, mobile and QA)

Only Docker Desktop (or Docker Engine with Compose v2) is needed: no .NET SDK, SQL Server or user-secrets. Ports 8080
and 1433 of the PC must be free. A local SQL Server default instance already listens on 1433: stop it first.

```bash
cp .env.example .env            # cmd.exe: copy .env.example .env. Local-only values; .env is git-ignored, never commit it
docker compose up -d --build    # first run builds the image and seeds the database (a few minutes)
```

Compose starts SQL Server, runs `seed --bootstrap --demo` once (migrations, the cooperative and the demo data of an
oyster cooperative in Vân Đồn, Quảng Ninh) and then starts the API in Development. The seed is idempotent: it runs again
on every `docker compose up` and creates nothing twice.

| What | Where |
|---|---|
| API | `http://localhost:8080` |
| Swagger | `http://localhost:8080/swagger` |
| Health | `http://localhost:8080/health/db` (database), `http://localhost:8080/health/ready` (deployment complete) |
| SQL Server (this PC only) | `localhost,1433`, user `sa`, password `MSSQL_SA_PASSWORD` from `.env` |

**Demo logins.** Every account below uses the password `Demo@1234`. It exists only in this local demo data.

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
| `nhanvien.hethan` | Zone Manager | DEMO-01, grant expired | Refused: no zone–role grant in effect |

A password you change stays changed: the seed never resets it. The Owner gets `Demo@1234` only while still on the
bootstrap temporary password. To get every demo login back, reset the environment with `docker compose down -v`.

The demo data also holds the cooperative profile, the zones `DEMO-01`/`DEMO-02` (In Use) and `DEMO-03` (Discontinued),
license documents (valid, expiring within 30 days, expired), about 10 rafts with lanes per zone, 8 employees (one Left),
5 suppliers, 15 farming input items and 20 purchase receipts of the last 3 months with payments, discounts and a return.
Phone numbers are fake (`0900 000 0xx`).

Dates are set relative to the day of the first seed and are not moved afterwards:
- After a few weeks the "expiring" license has expired. `docker compose down -v` gives fresh dates.
- Receipt codes carry the seed date (`PR-<seed date>-nnn`), not the receipt date.
- The receipt list shows the current calendar year by default. Seeded in January–March, part of the history is in the
  previous year: pass `from`/`to` to see it.

**Verification codes.** SMS, email and push use the fake providers: nothing leaves the machine. What would have been
sent stays in memory until the API restarts. Read it in the dev inbox, also listed in Swagger:
- `GET /api/dev/sms?phoneNumber=`
- `GET /api/dev/email?to=`
- `GET /api/dev/push?accountId=`

`docker compose logs -f api` shows each fake message's id and masked recipient, never the code.

For example, `nhanvien.chuaxacthuc` (phone `0900000015`) logs in with `phoneVerificationRequired = true`, and its
business calls answer MSG67 until the phone is verified. Logging in sends no code; with that login's access token:
1. `POST /api/me/phone-verification` with `{}` sends the code.
2. `GET /api/dev/sms?phoneNumber=0900000015` shows it.
3. `POST /api/verifications/{id}/verify` with `{ "code": "…" }` returns a verification token.
4. `POST /api/me/phone-verification/confirm` with `{ "verificationToken": "…" }` completes it.

The dev inbox is anonymous, so anyone on the same network can read the codes. Run this environment only on a network
you trust, never on public Wi-Fi.

**Phone on the same Wi-Fi.** The API listens on every interface of the PC. Find the PC's LAN IP (`ipconfig` on Windows,
`ipconfig getifaddr en0` on macOS) and point the mobile app at `http://<LAN IP>:8080`. If the phone cannot connect, allow
inbound TCP 8080 in the PC's firewall. The Android emulator reaches the PC at `http://10.0.2.2:8080`. File links (license
documents, raft drawings, invoice photos) are relative, signed paths such as `/api/files/local/...?expires=…&sig=…`.
Prefix them with the API base URL.

**Web app.** In Development `Cors:AllowedOrigins` allows `http://localhost:3000`, so the Next.js dev server can call the
API from the browser. For another origin, add a line such as `Cors__AllowedOrigins__1: http://192.168.1.10:3000` under
`environment` in `docker-compose.yml`, then run `docker compose up -d`.

**Update, reset, stop.**
- `docker compose up -d --build`: after a pull. It rebuilds the image, and the seed applies new migrations.
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

Email, SMS, push, OCR and file storage are adapters picked by configuration, so switching provider needs no code change.
In Development the fakes keep what they "send" in memory; read it at `GET /api/dev/sms?phoneNumber=`,
`GET /api/dev/email?to=`, `GET /api/dev/push?accountId=` (clear with `DELETE`). These endpoints answer 404 outside
Development; they are anonymous, so never expose a Development environment publicly. Local storage writes under `src/OMTS.Api/App_Data/files` (git-ignored); for Azurite set
`Storage:Provider=AzureBlob` and `Storage:AzureBlob:ConnectionString=UseDevelopmentStorage=true`.
The OCR Service of the water meter photos (ENV-01) is only faked until its provider ticket: the fake answers with
`Ocr:Fake` (default pH 7.8, salinity 25.5), unless the photo bytes contain a marker such as `ph9.1_sal20`, `conf0.6`,
`at2026-10-20T08:00`, `ocr-fail` or `ocr-timeout` (used by the tests).

### Background jobs

`WaterMeasurementOverdueJob` runs every day at 06:00 (Asia/Ho_Chi_Minh): each In Use zone without a Confirmed water
measurement for more than one calendar month gets one MSG26 reminder per overdue period (Owners and the zone's staff
with View on `WATER_MEASUREMENTS`). Like the zone license reminder (07:00), a host that starts after the run time runs
it once at start, so a restart never skips a day; a reminder already sent is never sent again.

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
   names of the missing keys. `--demo` also seeds the demo data of "Run with Docker" (Development only; refused
   in any other environment).
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
| OCR (`Ocr:Provider` unset by default: meter photos are not read and the values are entered by hand (MSG04); `Fake` invents readings, so it is set in Development only and stops the host elsewhere) | `Ocr:TimeoutSeconds` (default 15), `Ocr:UncertainBelow` (default 0.8), `Ocr:Fake:Mode` (`Success`, `Fail`, `Timeout`), `Ocr:Fake:Ph`, `Ocr:Fake:Salinity`, `Ocr:Fake:Confidence` |
| File storage (`Storage:Provider` = `Local` or `AzureBlob`; `Storage:Local:SigningKey` is required outside Development) | `Storage:Local:RootPath`, `Storage:Local:SigningKey`, `Storage:AzureBlob:ConnectionString`, `Storage:AzureBlob:Container` |
| Bootstrap (see Deployment) | `Bootstrap:Cooperative:FullName`, `Bootstrap:Owner:*` |
| Rate limits per client IP (an IPv6 client per /64) and minute | `RateLimits:CodeRequestsPerMinute` (default 10: every endpoint that sends or checks a code), `RateLimits:LoginsPerMinute` (default 30) |
| CORS: the web app calls the API from the browser. Set its origin in every deployed environment; empty means no CORS. Development allows `http://localhost:3000`. | `Cors:AllowedOrigins` (array, e.g. `Cors__AllowedOrigins__0=https://<web host>`) |
| Reverse proxy: X-Forwarded-For is trusted only from these addresses (and loopback), so the limits see the real client | `ForwardedHeaders:KnownProxies` (e.g. `10.0.0.4`), `ForwardedHeaders:KnownNetworks` (e.g. `10.0.0.0/24`) |
| Verification codes: SMS sent system-wide per 24 hours; beyond it SMS codes answer MSG04 and a critical log entry is written (one number or address gets at most 10 codes per hour) | `Verification:DailySmsLimit` (default 500) |
| Zone license reminder (FARM-03, daily, business time zone UTC+7) | `Jobs:ZoneLicenseReminder:RunAt` (default `07:00`), `Jobs:ZoneLicenseReminder:CatchUpOnStart` (default `true`: a host started after the run time runs once at start) |
| Overdue water measurement reminder (ENV-01, daily, business time zone UTC+7) | `Jobs:WaterMeasurementOverdue:RunAt` (default `06:00`), `Jobs:WaterMeasurementOverdue:CatchUpOnStart` (default `true`) |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — branches, Jira keys, commit/PR rules, releases.
