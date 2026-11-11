# Backend operations reference

Demo data, external-service adapters, deployment steps, configuration keys and the shared-server setup. Moved verbatim from the README on 11/11/2026; the README keeps the short how-to.

## Demo data (Docker)

A password you change stays changed: the seed never resets it. The Owner gets `Demo@1234` only while still on the
bootstrap temporary password. To get every demo login back, reset the environment with `docker compose down -v`.

The demo data also holds the cooperative profile (with a logo, a cover photo and two certifications: one Valid, one
expiring within 30 days), a fictional receiving bank account (`DEMO00000001`; skipped with a message when `Payments:Provider` does not accept it, as `None` does not), the zones `DEMO-01`/`DEMO-02` (In Use) and `DEMO-03` (Discontinued), zone licenses (`DEMO-01`:
an expired license replaced by one expiring within 30 days; `DEMO-02`: a valid license and an older expired one), about 10 rafts with lanes per zone (the first five of each In Use zone with an Open cultivation cycle, the first three with cultivation logs by the zone manager), 4 job types with hourly rates (one with a rate still
to come, one Discontinued), two versions of the attendance settings, 9 employees (codes NV000001–NV000009; one Left, one without a phone number; the 3 seasonal workers have job types),
payroll headers of the last two months without lines (Finalized, Awaiting finalization: a date up to the end of last month answers `MSG-LABOR-04`),
5 suppliers, 15 farming input items (codes VT001–VT015, every seed item counted in `con`) and 20 purchase receipts of the last 3 months
(16 Completed, 2 Cancelled, 2 Draft): Material lines and two seed receipts name the raft they were bought for, one line has a
fractional quantity (its amount is not a whole thousand), with a return and a return the Owner cancelled (`TH` codes). Payments to the
suppliers (`TT` codes, not linked to
receipts), one amount of money paid back and one payment cancelled feed the supplier debts (`GET /api/supplier-debts`). The Owner's
list holds a MSG-PURCHASE-01 notification for each completed receipt and a MSG-PURCHASE-04 for each transaction.
The demo data also holds 4 products (one Discontinued), 7 customer profiles (2 linked to the trader accounts, 2 the same buyer ready to merge) and
4 purchase requests of `thuonglai1`, one in each state (Awaiting response, Accepted for fewer kg with pay later, Declined, Cancelled).
An answer sends an SMS, so with an SMS sender other than Fake or None the requests stay unanswered and the seed output says so;
such a sender gets no mandatory SMS of the seed either (new purchase requests, water alerts).
`DEMO-01` and `DEMO-02` have water thresholds (pH 7.5–8.5, salinity 18–32 ‰, temperature 15–32 °C) and one water
measurement a day for the 30 days before the seed day, going round the rafts In Use of the zone, each with its temperature
and a placeholder meter photo: three readings outside the thresholds, one corrected reading (a salinity of 2.6 ‰ fixed to
26.0 ‰) and one voided reading. The mistyped 2.6 ‰ alerted before its correction, so the Zone Managers' inboxes hold four
sets of water alerts (the demo records as the Owner, who is not told of their own action). Phone numbers are fake
(`0900 000 0xx`).

Rafts `B1-01`, `B1-02`, `B2-01`, `B2-02` have material usage inside their Open cycles, and `B1-06`, `B2-06` a repair with no cycle, recorded by the zone's manager.
The first three rafts of each In Use zone have seeding sessions by the zone's manager: the first with a re-hung lane and its seeding marked complete (the Owner has a MSG-CYCLE-02), the second with a lane left Not seeded, the third with every lane seeded. The rafts `B1-04`, `B1-05`, `B2-04`, `B2-05` keep a cycle with no record, which can still be cancelled. Receipts with seeded lots can no longer be cancelled.
Rafts `B1-01` and `B2-01` have two harvest lots of the seed day (the second with `-2`), one lane Being harvested and one Harvested; `B1-02` has every seeded lane finished, so `quanlyvung1` has the reminder to close its cycle (MSG-CYCLE-08), and the Owner has a MSG-HARVEST-01 for each lot. Harvest settings are at their first-setup values, 50 kg per cage and 3 days. The demo lots are dated the seed day, so the daily close check leaves them for sale on the first start; three days later it warns the Zone Managers (MSG-HARVEST-05) and the day after it closes them (GFR-05).
`quanlyvung1` sells the first lot of `B1-01` in cash (`HD000001`) and ends the sale with the QR code: that lot is Published (TRACE-01) and the invoice has its QR link (sent to the fake inbox; with a real SMS sender the sale is left unfinished). `quanlyvung2` sells the first lot of `B2-01` part in cash, pay later (`HD000002`), left unfinished so that the end of the sale can be tried. Publish one lot from the Trace lot screen with `GET`/`POST /api/harvest-lots/{id}/publish-check|publish` (a Not published lot on an invoice; a Withdrawn lot, Owner only).

Dates are set relative to the day of the first seed and are not moved afterwards:
- After a few weeks the "expiring" license and certification have expired. `docker compose down -v` gives fresh dates.
- Receipt codes follow one sequence in the order they were seeded (`PN000001`…`PN000020`), not the receipt date, and so do
  payment codes (`TT000001`…) and return codes (`TH000001`…).
- The debt age and the current-month statement of a supplier move with the calendar; pass `from`/`to` to the statement.
- 
- The receipt list shows the current month by default (PURCHASE-03): pass `from`/`to` to see the older demo receipts.

### External services (development)

Email, SMS, push, OCR and file storage are adapters picked by configuration, so switching provider needs no code change.
In Development the fakes keep what they "send" in memory; read it at `GET /api/dev/sms?phoneNumber=`,
`GET /api/dev/email?to=`, `GET /api/dev/push?accountId=` (clear with `DELETE`). Every push is kept, and its `devices`
lists the tokens a real provider would deliver to: those registered (`POST /api/me/devices`) by a session that is still
logged in. These endpoints answer 404 outside
Development; they are anonymous, so never expose a Development environment publicly. Local storage writes under `src/OMTS.Api/App_Data/files` (git-ignored); for Azurite set
`Storage:Provider=AzureBlob` and `Storage:AzureBlob:ConnectionString=UseDevelopmentStorage=true`.
Fake OCR for water meter photos (WATER-01): the fake answers with
`Ocr:Fake` (default pH 7.8, salinity 25.5, temperature 28.5), unless the photo bytes contain a marker such as
`ph9.1_sal20_tmp28.5`, `conf0.6`, `at2026-10-20T08:00`, `ocr-fail` or `ocr-timeout` (used by the tests).
Payment Notification Service (API-12): interface `IPaymentNotificationService` and a fake that works in Development: it accepts every receiving bank account except the numbers of
`Payments:Fake:RejectAccounts` (default `REJECT000001`) and `Payments:Fake:TimeoutAccounts` (default `TIMEOUT00001`, never answers).
The incoming notifications (API-13) go through `IBankNotificationParser`, the provider's adapter, which checks the signature and
reads the transfers; the fake one reads the generic format below. `POST /api/dev/payment-notifications`
(`{ invoiceCode, amount, bankReference?, content?, accountNumber?, occurredAt?, outgoing? }`, anonymous, 404 outside Development)
fires a transfer to the receiving bank account through the same rules, so a VietQR sale can be finished without a bank; send the

## Deployment

There is no System Administrator inside the cooperative app: the cooperative and its first Cooperative Owner are
created by a one-time command run on the server by the deployment operator (not reachable over HTTP).

1. Set the secrets (environment variables or the hosting secret store; `__` replaces `:` in variable names):
   `ConnectionStrings:OmtsDb`, `Jwt:SigningKey`, `Provenance:PublicBaseUrl` (the absolute URL of the web app; the API does
   not start without it), the provider keys below, and the bootstrap keys
   `Bootstrap:Cooperative:FullName`, `Bootstrap:Owner:DisplayName`, `Bootstrap:Owner:Username`,
   `Bootstrap:Owner:PhoneNumber`, `Bootstrap:Owner:Email` (optional), `Bootstrap:Owner:TemporaryPassword`.
2. Run `dotnet OMTS.Api.dll seed --bootstrap` at handover time: it applies migrations, checks the status catalog, creates
   the rows of the two fixed roles (Zone Manager `ZONE_MANAGER`, Accountant `ACCOUNTANT`; their permissions are the
   static §I.4.4 matrix, never stored) and, if no cooperative exists, the cooperative (full name only) and the
   Owner with this first password. It must be changed at first login and does not expire. At that login the Owner first
   verifies the phone number by SMS (BR-ACCOUNT-03), so `Sms:Provider` must work before the handover. Running it again creates nothing that already exists ("already initialized", exit code 0); the roles are found by
   code. Missing secrets give exit code 1 and the
   names of the missing keys. Outside Development a database where any account holds the local demo password is refused
   (exit code 1, nothing written). `--demo` also seeds the demo data of "Run with Docker" (in Development, or in Staging as
   described in [Demo on a shared server](#demo-on-a-shared-server); refused in Production and any other environment).
   An Owner who cannot log in (for example after losing the first password) is recovered with
   `dotnet OMTS.Api.dll seed --reset-owner-password`. It sets `Bootstrap:Owner:TemporaryPassword` (from configuration, never
   from the command line, so it never lands in shell history) as a new password that must be changed at first login and
   does not expire, ends every session of the Owner and writes an audit row. Set a new value first: the Owner's current
   password is refused. With several Owners, `Bootstrap:Owner:Username`
   chooses one. Without an Owner, or without a valid password, it changes nothing and exits with code 1.
3. Start the API and check `GET /health/ready`: 200 when the database, migrations, status catalog, the two fixed role
   rows (by code), cooperative and an active Owner are all in place, else 503 listing the failing checks. This is a
   post-deployment verification, **not** a platform probe: it depends on business data (locking the only Owner turns it
   503). Point App Service / Kubernetes readiness and load-balancer probes at `GET /health/db`
   (database reachable and migrated). `GET /health` is liveness only. When Redis is configured but unreachable or empty,
   `/health/ready` still answers 200 and lists `session-revocation` as Degraded.


## Configuration keys

| Area | Keys |
|---|---|
| Email (`Email:Provider` = `Smtp` or `Fake`; `Email:Smtp:Security` = `StartTls` (587), `SslOnConnect` (465) or `None`, which is refused outside Development) | `Email:Smtp:Host`, `Port`, `Security`, `Username`, `Password`, `FromAddress`, `FromName`, `TimeoutSeconds`, `Email:Fake:FailFor` |
| SMS (`Sms:Provider` = `None`, the default: nothing is sent, phone verification cannot complete and each save with mandatory SMS writes one warning; `Fake` keeps messages in the dev inbox and is set in Development only) | `Sms:Fake:FailFor` (test numbers that fail), `Sms:MandatoryTimeoutSeconds` (default 5, from 1 to 60 or the host does not start: the longest the answer of an action waits for the SMS of its mandatory notifications) |
| Push (`Push:Provider` = `None` by default, `Fake` in Development) | `Push:Fake:FailFor` |
| OCR (`Ocr:Provider` unset by default: meter photos are not read and the values are entered by hand (MSG-WATER-01); `Fake` invents readings, so it is set in Development only and stops the host elsewhere) | `Ocr:TimeoutSeconds` (default 15), `Ocr:UncertainBelow` (default 0.8), `Ocr:Fake:Mode` (`Success`, `Fail`, `Timeout`), `Ocr:Fake:Ph`, `Ocr:Fake:Salinity`, `Ocr:Fake:Confidence` |
| Payment notifications, API-12 and API-13 (`Payments:Provider` = `None`, the default: no provider is chosen, so no receiving bank account can be accepted and the Owner gets MSG-DEBT-02, and every bank notification answers 401; `Fake` works in Development only and elsewhere acts as `None`) | `Payments:TimeoutSeconds` (default 15; 1 to 300, anything else stops the host at startup), `Payments:Fake:RejectAccounts`, `Payments:Fake:TimeoutAccounts`, `Payments:Fake:WebhookSecret` (a secret: user-secrets or `Payments__Fake__WebhookSecret`, empty by default, which accepts no notification) |
| File storage (`Storage:Provider` = `Local` or `AzureBlob`; `Storage:Local:SigningKey` is required outside Development; one container or folder per environment, see Background jobs) | `Storage:Local:RootPath`, `Storage:Local:SigningKey`, `Storage:AzureBlob:ConnectionString`, `Storage:AzureBlob:Container` |
| Zalo Mini App "Liên kết tài khoản" (`POST api/auth/zalo/link`, `zalo/login`, `DELETE api/me/zalo-link`; a trader without an account sends `zalo/trader-requests`, which the Cooperative Owner approves or refuses at `api/trader-requests`). `Zalo:Provider` unset by default: linking answers MSG04; `Graph` calls graph.zalo.me and needs the Zalo App secret (developers.zalo.me), the host does not start without it; `Fake` (Development only) accepts `fake:<zalo user id>` access tokens and `fake:<phone number>` phone tokens | `Zalo:Provider`, `Zalo:AppSecret` (user-secrets / `Zalo__AppSecret`), `Zalo:TimeoutSeconds` (default 10) |
| Bootstrap (see Deployment) | `Bootstrap:Cooperative:FullName`, `Bootstrap:Owner:*` |
| Rate limits per client IP (an IPv6 client per /64) and minute | `RateLimits:CodeRequestsPerMinute` (default 10: every endpoint that sends or checks a code), `RateLimits:LoginsPerMinute` (default 30), `RateLimits:ProvenancePagesPerMinute` (default 60: the public provenance page), `RateLimits:PaymentNotificationsPerMinute` (default 600: the bank notification webhook) |
| CORS: the web app and the Zalo Mini App call the API from the browser. Set their origins in every deployed environment; empty means no CORS. Development allows `http://localhost:3000` and `https://h5.zdn.vn`. | `Cors:AllowedOrigins` (array, e.g. `Cors__AllowedOrigins__0=https://<web host>`, `Cors__AllowedOrigins__1=https://h5.zdn.vn`) |
| Provenance link: the QR code of a sales invoice and its SMS (MSG-SALE-15) link to `{PublicBaseUrl}/origin/{qrToken}`, the public page of the web app. **Required** in every environment: without an absolute `http`/`https` URL the host does not start (QR codes are permanent, so a relative link is never made). Development uses `http://localhost:3000`. | `Provenance:PublicBaseUrl` (e.g. `Provenance__PublicBaseUrl=https://<web host>`) |
| Reverse proxy: X-Forwarded-For is trusted only from these addresses (and loopback), so the limits see the real client | `ForwardedHeaders:KnownProxies` (e.g. `10.0.0.4`), `ForwardedHeaders:KnownNetworks` (e.g. `10.0.0.0/24`) |
| Next.js server (BFF) in front of the API: it sends the end user's address and browser so the per-IP limits and the session list see the user, not the BFF. Off by default. Requests carrying `X-Bff-Secret` equal to the shared secret (constant-time compare) may set `X-Client-Ip` and `X-Client-User-Agent`; without a valid secret both are ignored, so an address cannot be chosen by the caller. The secret is **required, at least 32 characters, outside Development** when `Enabled` is true (the host does not start otherwise); in Development an empty secret leaves the feature inactive. The supplied address wins over `X-Forwarded-For`. | `BffClient:Enabled` (default `false`), `BffClient:SharedSecret` (a secret: user-secrets or an environment variable) |
| Refresh grace period: how long the refresh token a rotation replaced still gets an access token (see Authentication) | `Jwt:RefreshGracePeriod` (default `00:00:30`) |
| Revoked-session lookup in Redis instead of SQL on every request (see [Session revocation store](api-behaviour-reference.md#session-revocation-store-redis)). Optional: unset means SQL. Run Redis with a password, no persistence and `noeviction`; a lock done outside the application must also revoke the account's sessions | `ConnectionStrings:Redis` (a secret), `Revocation:Mode` (`Auto`, `Redis`, `Sql`), `Revocation:CommandTimeout` (default `00:00:00.250`), `Revocation:SweepInterval` (default `00:00:10`, 1 to 30 seconds) |
| Verification codes: SMS sent system-wide per 24 hours; beyond it SMS codes answer MSG-ACCOUNT-13 and a critical log entry is written (BR-ACCOUNT-09: one purpose and one number or address get at most 3 codes per hour, at least 60 seconds apart; over all purposes one number or address gets at most 10) | `Verification:DailySmsLimit` (default 500) |
| Expiry reminder of zone licenses and certifications (GFR-03, daily, business time zone UTC+7) | `Jobs:ZoneLicenseReminder:RunAt` (default `07:00`), `Jobs:ZoneLicenseReminder:CatchUpOnStart` (default `true`: a host started after the run time runs once at start) |
| Overdue water measurement reminder (GFR-04, daily, business time zone UTC+7) | `Jobs:WaterMeasurementOverdue:RunAt` (default `06:00`), `Jobs:WaterMeasurementOverdue:CatchUpOnStart` (default `true`) |
| Daily close check of harvest lots (GFR-05, daily, business time zone UTC+7) | `Jobs:HarvestLotAutoClose:RunAt` (default `00:05`), `Jobs:HarvestLotAutoClose:CatchUpOnStart` (default `true`) |
| Employment end: an employee is Left from the day after the last working day (LABOR-01, daily, business time zone UTC+7) | `Jobs:EmploymentEnd:RunAt` (default `00:01`), `Jobs:EmploymentEnd:CatchUpOnStart` (default `true`) |
| Login session clean-up: sessions that expired or were revoked more than `Retention` ago, with the device key, address and browser they hold (daily, UTC+7) | `Jobs:LoginSessionCleanup:Enabled` (default `true`), `RunAt` (default `03:30`), `CatchUpOnStart` (default `false`), `Retention` (default `30.00:00:00`, at least `1.00:00:00` or the host does not start) |
| Orphan file clean-up: raft photos, license files, cover photos, certificate files, cultivation log photos, meter photos (daily, UTC+7) | `Jobs:OrphanFileCleanup:Enabled` (default `true`), `RunAt` (default `03:00`), `GracePeriod` (default `1.00:00:00`, at least `01:00:00` or the host does not start) |

## Demo on a shared server

A server several people reach runs in **Staging**. **Never use `docker-compose.yml` as the shared server:** it runs in
Development and binds `0.0.0.0:8080`, so anyone on that network can read every verification code in the anonymous dev
inbox, open Swagger and log in with the password printed in this README.

`seed --bootstrap --demo` is decided before anything is written (`DemoSeedGate`), so run it first, on the empty database,
not after a plain `seed --bootstrap`:

| Environment | Demo data | Password of the demo accounts |
|---|---|---|
| Development | always | `Demo@1234`, as in "Run with Docker"; the two keys below are ignored |
| Staging | only with `Demo:AllowOutsideDevelopment=true` | the secret `Demo:Password` |
| Production, any other name | never, whatever the configuration says | – |

`Demo:Password` comes from the secret store of that server, never from a file in the repository. `appsettings.Staging.json`
and `appsettings.Production.json` are ignored by git and left out of the image for this reason; a settings file put beside
the program by hand is still read. The password is refused when it is empty, starts or ends with whitespace, is the
well-known password in any letter case, or breaks the password rule (8 to 128 characters with a letter and a digit). A
refusal gives exit code 1 and names the key, never the value.

To set up the server:
1. Set `ASPNETCORE_ENVIRONMENT=Staging` (and not a different `DOTNET_ENVIRONMENT`: when both are set and differ, the seed
   and the API refuse to start), the secrets of "Deployment" step 1 for an **empty** database, one storage folder or
   container of its own, `Demo__AllowOutsideDevelopment=true` and `Demo__Password`. Use fake data in `Bootstrap:*`.
2. Run `dotnet OMTS.Api.dll seed --bootstrap --demo`, start the API and check `GET /health/ready`.
3. Give the password to the testers privately. The nine logins of "Run with Docker" all use it, the Owner included, and
   land in the same branches. Put the server behind HTTPS: the image itself listens on plain HTTP.

**Which database.** The demo only runs on a database with no accounts, or on one it seeded itself: the first run writes a
`Demo.Seeded` audit row. Any other database with accounts, such as a copy of real data, one behind the migrations or a
mistaken connection string, is refused: exit code 1, nothing written, no migration applied. **Never run the demo seed on a
copy of real data: start from an empty database.** Refused too, by the demo seed and also by a plain `seed --bootstrap`
in any environment but Development: a database where any account holds `Demo@1234` (first seeded in Development, or
restored from such a copy). The message names the accounts, never a password. Starting the API does not check this, so a
copy restored onto the server and started without a seed still answers to the printed password.

**Reruns.** A rerun on the demo's own database adds only what is missing. It never touches the Owner's password, forced
change or verified phone, so `seed --reset-owner-password` stays as it was left, and the seed never changes a password it
has already set. So:
- A new `Demo:Password` changes no account: to change it, start again from an empty database.
- After a run that crashed half way, rerun with the same secret, or start from an empty database. A rerun with a rotated
  secret exits 0 and can leave two passwords: the accounts seeded before the crash stay on the old one.
- A tester who sets any account's password to `Demo@1234` makes the next seed refuse the whole server. The password rule
  does not forbid that value, and the printed password then logs in to that account: start again from an empty database.

**What stays off.** The dev inbox (`/api/dev/sms`, `/email`, `/push`) and Swagger answer 404 outside Development; no
setting opens them.

**SMS, email, push, OCR.** No real SMS or push provider exists yet, so a code cannot reach anyone on this server:
- The seeded accounts need no SMS: their phones are stored as verified. The exception is `nhanvien.chuaxacthuc`, which
  logs in with `phoneVerificationRequired = true`, gets MSG-ACCOUNT-04 on its business calls and cannot complete the step.
- Also out of reach: trader registration, password recovery by SMS, and the first login of any staff account created on
  the server (it must verify its phone). The Owner can still reset a staff password, and the forced password change of
  `nhanvien.doimk` works.
- Leave `Sms:Provider` and `Push:Provider` at `None` and `Ocr:Provider` unset. A code request then answers 503
  MSG-ACCOUNT-13, so nobody waits for a message that will never come.
- `Fake` is not refused for SMS, email or push: the host only logs a warning, the client is told the code was sent, and
  nobody can read it because the inbox answers 404. `Ocr:Provider=Fake` stops the host.
- `Email:Provider` defaults to `Smtp`. Without `Email:Smtp:Host` the host starts with no warning and each send fails
  (503). The demo accounts have no email address, so the demo does not need it.
- When a real SMS provider arrives, do not point it at this data as it is: the demo phone numbers (`0900 000 0xx`) are
  made up and may belong to someone.

**No real bank account or payment-service key.** This is a rule for the deployer, not a check: the code has no payment
service, so there is no key to configure, store or refuse. The seed reads only `Bootstrap:*` and `Demo:*`; the bank
numbers of its employees are placeholders in `DemoEmployeePlans`, and it stores no cooperative bank account. Never enter
a real bank account on this server, in an employee record or anywhere else. Whoever adds a payment provider also adds
the refusal to `DemoSeedGate`.
