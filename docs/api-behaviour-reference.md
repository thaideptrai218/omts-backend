# Backend behaviour reference

Detailed rules of the API modules: authentication and sessions, roles, notifications, public provenance, sales invoices, quality claims, workforce, audit log, VietQR sales and background jobs. Moved verbatim from the README on 11/11/2026; the README keeps setup and how-to only. Change notes against SRS v8 are in the Web repository `docs/SRS_DEVIATIONS.md`.

### Authentication

`POST /api/auth/login` (a phone number or a username, and the password) returns a 15-minute access token and a
refresh token (7 days web / 30 days mobile, rotated on every `POST /api/auth/refresh`); `POST /api/auth/logout`
revokes the session. Every `AuthTokens` response (login, refresh, `zalo/login`; also the `tokens` object of a password
change) has the field `refreshTokenRotated`. Normally it is `true`: `refreshToken` is a new token, and after a refresh
the old one stops working. If the client sends the token that its previous refresh just
replaced (the response was lost, or two requests carried the same token), it gets, for 30 seconds (`Jwt:RefreshGracePeriod`),
a new access token and the token it sent, echoed back with the session's current `refreshTokenExpiresAt`, and
`refreshTokenRotated` is `false`: no new refresh token is issued, the session is not rotated again, and the client keeps
the token it already holds (a web server must not write cookies for such a response). The session, the account status and
the effective grants are checked as for a rotation. Any other old token, or the replaced one after the grace period, is
replay and revokes the session. A refresh response that never arrives is not repaired: the client that kept the old token
ends the session when it uses it after the grace period and must sign in again. An access token never outlives its
session: its `exp` is the earlier of the 15-minute lifetime and the session's absolute expiry. Token validation accepts
30 seconds of clock skew, so a token can be accepted up to 30 seconds after its `exp`, and the session's absolute expiry
can be overrun by up to 30 seconds. Locking an account
revokes all its sessions in the same save, and a login that races the lock revokes the session it just stored.

**Login identifier.** A phone number or a username; an email address is never an identifier, so logging in with one fails
like any unknown identifier. A phone number logs in whether or not it is verified (the password proves who the person is);
an unverified phone then meets the phone-verification gate (`AccountStateGateFilter`, MSG-ACCOUNT-04): every business call
is refused until the phone is verified. Email is not an identifier for sign-in, password recovery or registration. See the Web repository `docs/SRS_DEVIATIONS.md` (section Đăng nhập và tài khoản).

**Devices and sessions.** `POST /api/auth/login`, `zalo/login` and `zalo/link` accept an optional `deviceId`: a random id
the browser or app install keeps, base64url, at most 128 characters; missing, empty or `unknown` means no device. The server stores only
a keyed hash of it (`DeviceKey`, derived from the signing key), never the id, and never uses it to authenticate. Sessions
without a device are never grouped. A session also records how it was opened (`AuthMethod`: `Password` or `ZaloMiniApp`) and why it
ended (`RevokeCause`: `Revoked` or `TokenReuse`). Under `/api/me`, for the caller's own account only (another account's session answers 404):

| Request | Answer |
|---|---|
| `GET sessions` | Open sessions, newest first: `id, clientType, authMethod, deviceKey, deviceName, userAgent, maskedIpAddress` (`203.0.*.*`), `signedInAt, lastActiveAt, expiresAt, isCurrent` |
| `DELETE sessions/{id}` | 204, also when the session had already ended; the current session may be ended (the next request is 401) |
| `POST sessions/revoke-others` | 200 `{ "count": n }`: every open session except the current one |
| `GET devices` | Open sessions grouped by `deviceKey` (`deviceKey, clientType, authMethod, deviceName, userAgent, maskedIpAddress, firstSignedInAt, lastActiveAt, isCurrent, sessionIds`); a session without a device is its own entry with a null `deviceKey` |
| `DELETE devices/{deviceKey}` | 204 always (idempotent): ends the caller's sessions on that device. `deviceKey` is 43 base64url characters; the route shares its prefix with `DELETE devices/{token}` (push token), which keeps any other shape |

Revoking goes through the same path as logout, so the next request of an ended session is 401 (through Redis when configured). A
password change keeps the device and sign-in method of the session it replaces. `DELETE /api/me/zalo-link` ends every `ZaloMiniApp`
session of the account, on every device, and the current session.

**Refusing a refresh.** A refused `POST /api/auth/refresh` keeps its status and problem shape (401 MSG19; 403 when the account may no
longer log in) and adds the problem extension `reason`: `expired` (the lifetime ended, or the session no longer exists),
`reuse` (a replaced token was presented again, so the session is revoked) or `revoked` (a user or an administrator ended it: logout
elsewhere, a session or device revoked, a lock, a password change). A Mini App signs in again by itself after `expired` or `reuse` only.
`POST /api/auth/zalo/login` with a `deviceId` whose newest session of that account was revoked by a user or an administrator answers
403 `ZALO_DEVICE_REVOKED`; only `POST /api/auth/zalo/link`, which creates a newer session, lets the device in again.
A session revoked for reuse does not block silent login. Sessions are deleted 30 days after they expired or were revoked
(`Jobs:LoginSessionCleanup`), and from then on a device that was signed out is no longer remembered as such.

`GET /api/me` and `GET /api/me/permissions` return the caller and their effective
zone-scoped permissions. `GET /api/me` also carries `mustChangePassword` and `phoneVerificationRequired`, the same
account state the gate behind the 403 `PASSWORD_CHANGE_REQUIRED` / MSG-ACCOUNT-04 answers uses, and stays reachable while
either is true, so the web can route onboarding without waiting for a 403. Both can be true at once; the phone step
comes first. Errors are RFC 7807 problem details whose `title` and `code` are the message code of
the SRS 27/10/2026 catalogue (`MSG-GEN-01`, `MSG-ACCOUNT-05`…) and whose `detail` is the SRS English text.   Success responses that carry a text carry its code next to it (`messageCode`,
`noticeCode`).

Dates and times are ISO 8601. A time sent without a UTC offset (`2026-10-20T08:00:00`, in JSON or a multipart field)
is Vietnam time (UTC+7), never the server's time zone or UTC; `Z` or an offset such as `+07:00` is kept. Responses always
carry the offset. Business dates (`from`/`to` filters, receipt dates) are Vietnam dates.

#### Session revocation store (Redis)

Every authenticated request checks that its session is not revoked. With Redis configured, this is a lookup of
`omts:auth:rev:s:{sessionId}` instead of a database query. The database stays the source of truth:
- A saved revocation (logout, password change or reset, lock, refresh token reuse) is written to Redis right after the
  commit, with a time to live of the access token lifetime plus one minute (about 16 minutes). Keys hold session ids only.
- Every instance also copies the sessions revoked in that window from the database every 10 seconds
  (`Revocation:SweepInterval`) and then renews the marker `omts:auth:ready` (60 seconds). A write that failed after the
  commit is repaired by the next sweep, which is started at once; a revocation can therefore reach Redis up to about 10
  seconds late when Redis was unreachable at that moment.
- A request is answered from Redis only while the marker exists. When Redis is unreachable, slow (`Revocation:CommandTimeout`,
  250 ms; three failures within 5 seconds stop all Redis calls for 10 seconds), empty after a restart or flush, or the
  marker has expired, the request is checked against the database as before: a revoked session is still refused, a valid
  one still accepted. `/health/ready` lists `session-revocation` as Degraded (still 200) in that state; it is not part of
  `/health` or `/health/db`.
- **A lock performed outside the application** (a trader account locked by a direct database change, any other script)
  **must also set `RevokedAt` on that account's `LoginSessions`**. The Redis path no longer reads the account status, so
  without it the account keeps working for up to 15 minutes (until the access token expires; a refresh is refused at
  once). Locking in the application does both in one save.
- Revocations must be saved through EF Core entities. A bulk `ExecuteUpdate` on `LoginSessions` bypasses the write after
  commit and would be seen only by the next sweep.

Configuration: `ConnectionStrings:Redis` (a secret, e.g. `redis:6379,password=...`; empty by default), `Revocation:Mode`
(`Auto`, the default: Redis when the connection string is set, otherwise the database; `Redis`: the host does not
start without the connection string; `Sql`: the database always), `Revocation:CommandTimeout`, `Revocation:SweepInterval`
(1 to 30 seconds). Without a connection string nothing changes compared with before. Run Redis without persistence and
without eviction (`maxmemory-policy noeviction`): evicting a key would silently drop a revocation until the next sweep
notices, and a restart is harmless because the sweep refills it. `docker-compose.yml` does this.

### Employee app login (LABOR-03, LABOR-06)

An employee has no account. The employee app is a second kind of caller with tables of its own (`EmployeeLoginCodes`,
`EmployeeDevices`, `EmployeeSessions`), a token of its own and an authentication scheme of its own, `EmployeeApp`.

**Login code (LABOR-03).** `POST /api/employees/{id}/login-codes` (no body): the Cooperative Owner, or a Zone Manager who maintains the
employee (a working zone among the zones of their pairs; `LABOR_WORKERS` Create). Another caller is 403 MSG-GEN-08, an unknown employee 404.
201 `{ code, expiresAt, phoneNumber, employeeName, message, messageCode }` (MSG-LABOR-08); a Left employee, or one without a phone number, is
409 MSG-LABOR-09 with `reason` `Left` or `NoPhone`. A code has 6 digits, works once, expires after 5 minutes and is cancelled by the 5th
wrong entry; a new code cancels the earlier one (a filtered unique index keeps one open code per employee, also under parallel issuers).
Only a keyed hash of it is stored (`IVerificationCodeHasher`, keyed by the row id with a key derived from `Jwt:SigningKey`), and it is never
written to the audit log (`Employee.LoginCodeIssued` holds the expiry only) or to the logs. It is shown in this answer and sent to the
issuer's own account as the notification `EMPLOYEE_LOGIN_CODE` (kept in their list, pushed unless they turn it off; its text holds the code,
as MSG-LABOR-08 asks and for those 5 minutes); it is never sent to the employee. The answer is `Cache-Control: no-store` (and `Pragma: no-cache`),
like the login and refresh answers that carry tokens. Rate limited per signed-in account, not per IP and apart from the anonymous code endpoints:
`RateLimits:EmployeeLoginCodesPerMinute` (default 60, so a Zone Manager can issue a code for a whole crew; 429 `TOO_MANY_REQUESTS`). The limiter
runs after authentication for this reason; a caller without an accepted token falls back to a per-IP bucket (and gets 401 anyway).
When the 5th wrong entry cancels the open code, one audit row `Employee.LoginCodeCancelled` is written (the expiry and the number of tries, no
code) and the account that issued the code gets a notification of type `EMPLOYEE_LOGIN_CODE` (message code MSG-LABOR-19, as the SRS has no
message of its own for it; the text names the employee and holds no code). A phone number change or an end of employment also cancels the open
codes. A per-employee limit on wrong entries across codes is not applied: a gap between wrong entries would have to skip the check of the code
to stop a guess and would also lock out a real employee typing right after an attacker's wrong entry; the audit row and the notice are the
countermeasure, and 5 tries over 10^6 codes keeps guessing hopeless.

**Login (LABOR-06).** `POST /api/employee-app/login`, anonymous, `RateLimits:EmployeeAppLoginsPerMinute` per client IP (default 30, a policy of its
own, not shared with the staff and Zalo logins; 429 `TOO_MANY_REQUESTS`); the answer is `Cache-Control: no-store`.
Body `{ phoneNumber, deviceId?, deviceSecret?, loginCode?, platform?, deviceName? }`; the phone number is normalised like every other one.
1. No employee who is Employed today has the number: 401 MSG-LABOR-18 (an unparsable number is 400 MSG-GEN-01).
2. `deviceId` and `deviceSecret` of the employee's linked device: logged in with no code (LABOR-06.1). The secret is **rotated at every such
   login**: the answer carries the new `deviceSecret`, which the app must store in place of the old one. Only one session per device stays open
   (the login ends the earlier ones). The secret the last login replaced is kept as a hash; if it comes back (an app copied to another phone, or a
   login whose answer was lost and then repeated), the device is unlinked with all its sessions, `Employee.DeviceUnlinked` is audited with the
   reason "copied", and the answer is 401 MSG-LABOR-17: a new login code is the way back. A secret older than that is just wrong (also 401 MSG-LABOR-17).
3. No `loginCode`: 401 MSG-LABOR-17 with `loginCodeRequired: true`.
4. A code that is wrong, expired, spent or cancelled: 401 MSG-LABOR-19 (all the same answer; a wrong entry counts one of the 5 tries, an
   expired code counts none). `platform` (`ios` or `android`) is checked first, 400, so a request that cannot link a device uses no try; so is
   the format: an entry that is not exactly 6 digits is 400 MSG-GEN-01 and counts no try.
5. A correct code is spent, the device linked before (if any) is unlinked with all its sessions, and this device becomes the linked one.

Answer: `{ accessToken, accessTokenExpiresAt, refreshToken, refreshTokenExpiresAt, refreshTokenRotated, employee { id, fullName, type },
deviceId, deviceSecret, qrKey, qrStepSeconds, serverTime }`. `deviceSecret` (256 random bits, stored only as a SHA-256 hash) is in the answer
that linked the device and in the answer of each code-less login (which replaces it), nowhere else: the app keeps the latest with `deviceId` to log in again without a code. A refresh does not rotate it. `qrKey` is derived from the
signing key and the device id, the same at every login of that device; the app signs its attendance QR code with it (LABOR-07, `qrStepSeconds`
60), and `serverTime` lets it keep its clock in step offline. Linking audits `Employee.DeviceLinked` with the employee as the person (GBR-02).

**Session.** The access token lasts 15 minutes. The refresh token (`POST /api/employee-app/refresh`, anonymous) lasts 30 days, sliding, 90 days
at most (`Jwt:EmployeeRefreshTokenLifetime`, `Jwt:EmployeeSessionMaxLifetime`) and follows the rules of a staff refresh token: rotated on every
refresh, the one it replaced is answered for `Jwt:RefreshGracePeriod` with `refreshTokenRotated: false`, any other old token is replay and ends
the session; a refusal is 401 MSG19 with `reason` `expired`, `reuse` or `revoked`. `POST /api/employee-app/logout` ends the current session and
keeps the device linked, so the next login on it needs no code. `PUT /api/employee-app/device/push-token` `{ pushToken, pushPermissionGranted }`
sets the push token of the caller's device (empty clears it; at most 500 characters). Every request of the employee app asks the database
whether the session is open, its device is still linked and the employee is Employed today (the day after the last working day, before the daily
job has run, already counts as Left); employee sessions are not in the Redis revocation store.

| Event | Sessions | Device |
|---|---|---|
| Logout | the current one | kept |
| A code-less login on the linked device | the earlier ones (one session per device) | kept; secret rotated |
| The replaced device secret presented again (a copy of the app) | all | unlinked (`CloneSuspected`) |
| A code entered on another device | all | unlinked (`NewDevice`) |
| The phone number of the profile changed or cleared (`PUT /api/employees/{id}`) | all; open login codes cancelled | unlinked (`PhoneChanged`) |
| End of employment: a last working day before today, or the day after it (`EmploymentEndJob`) | all; open login codes cancelled | unlinked (`EmploymentEnded`) |
| Resume | none (already unlinked) | a new code is needed |

Each unlink is one audit row `Employee.DeviceUnlinked` (the person is the caller, the employee, or the system for the job).

**Token isolation (BR-LABOR-02).** An employee token has the audience `Jwt:EmployeeAudience` (`omts-employee-app`, never equal to `Jwt:Audience`:
the host does not start otherwise), is signed with a key derived from `Jwt:SigningKey` with a label of its own, and carries `employee_id`,
`employee_session_id`, `employee_device_id` and `subject_type = Employee` but no `sub` and no `sid`. The default scheme (every plain
`[Authorize]`, so every staff endpoint) refuses it, and only the endpoints that name the scheme
(`[Authorize(AuthenticationSchemes = EmployeeAppAuthentication.Scheme)]`, today the `EmployeeAppController`) accept it and refuse a staff token. `ICurrentUser.AccountId`
is null for an employee request, `ICurrentEmployee` (employee, session, device) is null for every other one. Tests sweep every endpoint in both directions.

**Push to employees (BR-LABOR-04).** `IEmployeeNotifier` queues a push for the employee's linked device while one of its sessions is open and
the device allows notifications; it keeps nothing (no account, no list). The development inbox lists it with `GET /api/dev/push?employeeId=`.

### Roles and zone access

OMTS has three fixed roles (BR-FARM-07): Cooperative Owner, Zone Manager and Accountant, and a fourth login-only role `EMPLOYEE` for field employees (see Seasonal workers below). They are not created, edited or
discontinued: `GET /api/roles` lists the staff roles with their `code` (`ZONE_MANAGER`, `ACCOUNTANT`, `EMPLOYEE`) and their
permissions, the static matrix of SRS §I.4.4 in `FixedRoles`. The Owner grants a staff account zone–role pairs
(`POST /api/accounts/{id}/grants` with `role` (code) or `roleId`, and a `zoneId` or none for All zones). A pair is Effective
until it is revoked with a reason; there are no validity dates. On records with a zone a role works only in the zones of
its pairs; records with no zone (suppliers, supplier payments and debts, customers,
payroll…) follow the role only, so an Accountant pair for one zone opens them (BR-FARM-08); purchase receipts and returns carry an optional zone and a Zone Manager sees only those of its zones (see Purchasing of a Zone Manager below). The rafts a Zone Manager names on
a purchase receipt line are still those of the zones of their Zone Manager pairs. A staff account without an Effective pair
cannot log in.

Selling is not the Zone Manager's (owner decision of 20/11/2026, reversing 27/10 and 02/11): `ZONE_MANAGER` holds no `SALES_ORDERS`, `SALES_INVOICES`, `SALES_RETURNS`, `CUSTOMERS`, `QUALITY_CLAIMS` or `PURCHASE_REQUESTS`. It keeps cultivation, water,
harvest (`HARVESTS`, `HARVEST_LOTS`, `HARVEST_PLANS` View), `LOT_TRACING`, `PROVENANCE`, `PURCHASE_RECEIPTS`, `SUPPLIERS`, input and product disposals, employees, work assignment and attendance. Wherever this reference below says the Zone Manager sells,
views or answers sales records (invoices, resend, sales returns, quality claims, customers, orders, purchase requests, the sale at the port), read it as Owner only (Accountant unchanged: view and export of invoices, orders and customers as listed). `GET /api/me/permissions` of a Zone Manager no
longer lists those functions. The lot trace and the reverse trace by seed lot still show the invoices that sold a lot (code, kg) to a Zone Manager but no price, amount or trader (those follow `SALES_INVOICES` View).

### Farming zones and licenses

- Zones (FARM-02): `GET /api/zones/next-code` (Owner) proposes the next code `KV01`, `KV02`… (GBR-03: the highest `KVnn` in
  use, discontinued zones included, plus one); the Owner may save another code. A Discontinued zone is read-only: its
  edit answers MSG-GEN-09 until `POST /api/zones/{id}/reactivate`.
  The water measurement point is optional. Create and edit answer `{ zone, message, messageCode }` (MSG-GEN-05).
- Zone boundary (PM decision 25/11/2026, warn, never block): `PUT /api/zones/{id}/boundary` (Owner, In Use zone, `rowVersion`
  as the edit) with `{ boundary, rowVersion }` saves the licensed sea area the app read from the photographed map, as RFC 7946
  GeoJSON in WGS84 (positions `[longitude, latitude]`): a `FeatureCollection` of 1 to 10 `Feature`s, each a `Polygon` of one
  ring (no holes) of 3 to 100 distinct corners, inside Vietnam; `properties` `{ name, corners }` is optional (name up to 100
  characters, one label of up to 20 per corner). The server rounds to 6 decimals, drops an altitude, closes an open ring and
  keeps the ring order; `boundary: null` clears it. A refusal is MSG-GEN-01 naming `Boundary`, `Boundary coordinates`,
  `Boundary name` or `Boundary corners`. The answer is `{ zone, message, messageCode, warnings }`; `zone.boundary`, also in
  `GET /api/zones/{id}` and `GET /api/zones/map`, is the saved GeoJSON or null. `RAFTS_OUTSIDE_ZONE_BOUNDARY`
  (`details: { count, raftCodes }`) names the In Use rafts pinned outside the new boundary. A raft create or edit pinned more
  than 1 m outside every area answers `RAFT_OUTSIDE_ZONE_BOUNDARY` (`details: { distanceM }`, whole metres to the nearest
  edge), besides `RAFT_FAR_FROM_ZONE`.
- Licenses (FARM-03): a license belongs to one zone (`zoneId`) and is added, edited or replaced only while that zone is In Use (MSG-GEN-09). The expiry date is required;
  the type, holder, legal scope and the scan file are optional (on create, edit and replace). A license is Replaced from the day the license replacing it is recorded,
  whatever its dates (BR-FARM-06); a Replaced license cannot be edited or replaced again (MSG-GEN-09).

### Notifications

Every notification a module sends is kept in the list of its recipient account, whatever the preferences and even when
it could not be pushed (BR-ACCOUNT-12). Each type of `NotificationTypes.All` has a push policy and the roles it is sent
to (BR-ACCOUNT-11); a module that sends a new type registers it there, and the dispatcher refuses unregistered types.

| Type | Content | Push | Roles |
|---|---|---|---|
| `WATER_ALERT` | MSG-WATER-03 | always (mandatory) | Owner, Zone Manager |
| `WATER_ALERT_WITHDRAWN` | MSG-WATER-04 | always (mandatory) | Owner, Zone Manager |
| `WATER_MEASUREMENT_OVERDUE` | MSG-WATER-06 | unless turned off | Zone Manager; Owner when the zone has none |
| `ZONE_LICENSE_EXPIRY` | MSG-FARM-01 / MSG-FARM-02 | always (mandatory) | Owner |
| `COOPERATIVE_CERTIFICATION_EXPIRY` | MSG-FARM-01 / MSG-FARM-02 | always (mandatory) | Owner |
| `STAFF_ACCOUNT_LOCK_REMINDER` | MSG-LABOR-02 (LABOR-01.3) | never: list only | Owner |
| `EMPLOYEE_LOGIN_CODE` | MSG-LABOR-08, the login code of the employee app (LABOR-03), only to the account that issued it | unless turned off | Owner, Zone Manager |
| `EMPLOYEE_REQUEST_NEW` | MSG-LABOR-23 (LABOR-10), when an employee sends a request (leave, missed check-in or check-out) | unless turned off | Owner |
| `CULTIVATION_LOG_RECORDED` | MSG-CYCLE-04, when a cultivation log is saved, not on a correction, not to the Owner who recorded it | unless turned off | Owner |
| `CYCLE_SEEDING_COMPLETED` | MSG-CYCLE-02, when the seeding of a raft is marked complete (once per cycle), not to the Owner who marked it | unless turned off | Owner |
| `HARVEST_LOT_RECORDED` | MSG-HARVEST-01, when a harvest lot is recorded, not on a correction, not to the Owner who recorded it | unless turned off | Owner |
| `HARVEST_PLAN_HANDED_OVER` | a Zone Manager handed a harvest plan over ("Bàn giao", 24/11/2026), not to an Owner who did it; message code `MINI_APP_NOTIFICATION`; opens the plan | unless turned off | Owner |
| `PURCHASE_SHIFT_ASSIGNED` | the Owner or the manager responsible chose the employee for a purchase request: the shift is already a work assignment (24/11/2026), nothing to answer; message code `MINI_APP_NOTIFICATION`; opens the request | unless turned off | Employee |
| `LANE_DAMAGED` | a lane of a raft is reported damaged (12/11/2026), not to the Owner who reported it; message code `MINI_APP_NOTIFICATION` (no SRS message exists); opens the raft | unless turned off | Owner |
| `LANE_OUT_OF_SERVICE` | a lane is put out of service (12/11/2026), not to the Owner who did it; message code `MINI_APP_NOTIFICATION`; opens the raft | unless turned off | Owner |
| `LANE_PROGRESS_CHANGED` | the seeded or harvested percentage of a lane is changed by hand, `PUT /api/rafts/{raftId}/lanes/{n}/progress` (12/11/2026), not to the Owner who did it; message code `MINI_APP_NOTIFICATION`; opens the raft | unless turned off | Owner |
| `NURSERY_RECORDED` | a nursery batch (ươm giống) is started, edited, cancelled, completed or its survivors or shares corrected (12/11/2026), not to the Owner who did it; message code `MINI_APP_NOTIFICATION`; opens the batch | unless turned off | Owner |
| `HARVEST_LOT_AUTO_CLOSE_WARNING` | MSG-HARVEST-05, from the daily close check (GFR-05), once for each close date of a lot still for sale that the next check closes: to the Zone Managers of the zone (All zones too), or to the Owners when it has none | unless turned off | Owner, Zone Manager |
| `CYCLE_CLOSE_REMINDER` | MSG-CYCLE-08, once per cycle, when a harvest makes every seeded lane Harvested: to the Zone Managers of the zone (All zones too), or to the Owners when it has none, not to the Owner who recorded it | always (mandatory) | Owner, Zone Manager |
| `PURCHASE_RECEIPT_COMPLETED` | MSG-PURCHASE-01, when a receipt is completed (never for a draft), also when the Owner completed it | unless turned off | Owner |
| `PURCHASE_RECEIPT_CANCELLED_ADVANCE` | MSG-PURCHASE-08, when a cancelled receipt leaves the supplier debt below zero | unless turned off | Owner, Accountant |
| `SUPPLIER_TRANSACTION` | MSG-PURCHASE-04, for a supplier payment, money paid back or a cancelled one, also when the Owner did it | always (mandatory) | Owner |
| `CUSTOMER_PAYMENT_TRANSACTION` | MSG-DEBT-03, for a customer payment, money paid back or a cancellation saved in DEBT-02, and one notice per list of confirmations (no related record when the list holds several payments), also when the Owner did it. A cancellation of a payment found Confirmed, with no `rowVersion` sent (it may have lost a race with a confirmation), is allowed and writes a second audit row `CustomerPayment.CancelledAfterConfirm` (payment code, reason, who confirmed it and when) beside `CustomerPayment.Cancelled` | always (mandatory) | Owner |
| `PROVENANCE_WITHDRAWN` | MSG-TRACE-05, when the Owner withdraws the origin information of a lot the trader bought (TRACE-04); the row opens nothing | unless turned off | Trader |
| `TRADER_ACCESS_REQUESTED` | a trader asked for an account from the Zalo Mini App (`POST /api/auth/zalo/trader-requests`) | unless turned off | Owner |
| `TRADER_ACCESS_APPROVED` | the Owner approved the request (`POST /api/trader-requests/{id}/approve`) | unless turned off | Trader |
| `QUALITY_CLAIM_NEW` | MSG-CLAIM-01, a new quality claim (CLAIM-01) | unless turned off | Owner |
| `SALES_RETURN_RECORDED` | MSG-CLAIM-05, a new sales return (CLAIM-03); to the Accountants, only when money is owed back, the same message goes as `CUSTOMER_MONEY_OWED_BACK` | unless turned off | Owner |
| `QUALITY_CLAIM_RESPONSE` | MSG-CLAIM-02, the Owner's response (CLAIM-02); also sent as an SMS to the phone number of the profile | unless turned off | Trader |

- Preferences (ACCOUNT-07): `GET`/`PUT /api/me/notification-preferences` list the types of the roles the account
  holds with `push` on or off (`items: [{ type, push }]`). Mandatory types are shown as always pushed and cannot be
  turned off; list-only types are not offered. `pushAllowedOnDevice` is false when no device registered by a logged-in
  session allows notifications; turning push on then still saves the choice and answers MSG-ACCOUNT-21 in `notice`.
- List (ACCOUNT-08): `GET /api/me/notifications?unreadOnly=&page=&pageSize=` (newest first, `unreadCount` of the whole
  list), `POST /api/me/notifications/{id}/read`, `POST /api/me/notifications/read-all`. A row keeps its English
  `title`/`body` and the `messageCode` with `messageParams`, a JSON object of atomic values (never an English phrase) from
  which the apps build the text in Vietnamese or English; a push carries the same code and parameters. Dates are ISO
  dates, times ISO 8601 with the Vietnam offset:

  | Code | `messageParams` |
  |---|---|
  | MSG-WATER-03 | `corrected` (bool), `zone_name`, `raft_code` (null: no raft yet, "–"), `measurement_time`, `values_outside`: `[{ indicator: PH \| SALINITY, value, unit: "‰" \| null, direction: High \| Low, min, max }]` |
  | MSG-WATER-06 | `zone_name`, `last_date` |
  | MSG-FARM-01 / MSG-FARM-02 (`ZONE_LICENSE_EXPIRY`) | `document_type` (license type code, null when none), `document_number`, `zone_codes` (list holding the license's one zone), `expiry_date`, `days` (MSG-FARM-01 only) |
  | MSG-FARM-01 / MSG-FARM-02 (`COOPERATIVE_CERTIFICATION_EXPIRY`) | `certification_name`, `certification_number` (null when none), `expiry_date`, `days` (MSG-FARM-01 only) |
  | MSG-CYCLE-02 | `raft_code`, `seeded_count`, `lane_count` (lanes In Use) |
  | MSG-CYCLE-04 | `raft_code`, `visit_date` |
  | MSG-HARVEST-05 | `lot_code`, `raft_code`, `close_date` (the day of the check that closes the lot, at its start) |
  | MSG-PURCHASE-01 | `user_name`, `receipt_code`, `supplier_name`, `line_count`, `total_amount` (whole VND: the sum of the lines, each rounded half up to the dong) |
  | MSG-PURCHASE-04 | `user_name`, `transaction_kind` (`Payment`, `PaidBack`, `PaymentCancelled`, `PaidBackCancelled`), `payment_code`, `amount`, `supplier_name`, `debt_amount` (the debt after it; below zero: paid in advance) |
  | MSG-PURCHASE-08 | `user_name`, `receipt_code`, `supplier_name`, `advance_amount` (VND paid in advance, positive) |
  | MSG-DEBT-03 | one transaction: `user_name`, `transaction_kind` (`Payment`, `PaidBack`, `PaymentCancelled`, `PaidBackCancelled`), `payment_code`, `amount`, `customer_name` (null: no customer profile), `debt_amount` (the debt after it; below zero: money owed back; null without a profile); a list of confirmations: `user_name`, `transaction_kind` = `Confirmations`, `confirmed_codes` (the first 10), `confirmed_count`, `confirmed_more_count`, `confirmed_amount`, `cancelled_codes` (the first 10), `cancelled_count`, `cancelled_more_count`, `cancelled_amount`, `amount` (both together), `debts`: `[{ customer_name, debt_amount }]` after the cancellations (the first 5), `debts_more_count`; the text names the same ("PT000001, …, PT000010 and 189 more"), so a list of 200 fits the notification row |
  | MSG-TRACE-05 | `cooperative_short_name`, `lot_code`, `invoice_codes` (list), `cooperative_phone` (null: none set up yet, the text says "the cooperative") |
- `GET /api/me/notifications/{id}/open` checks the access again (GFR-02): 403 MSG-GEN-08 when the user may no longer
  open the record (e.g. a revoked zone–role pair), 404 MSG-ACCOUNT-22 when the record was removed. A push carries the
  notification id.
- The same table keeps SMS and Zalo OA sendings to traders (`channel`, phone, delivery status Sending / Sent / Not
  sent, provider message id or error code); they are never in a list. The response to a purchase request (SALE-03) uses
  them through `ITraderMessageSender`, whose only route is SMS (`Sms:Provider`, Fake or None) until the Zalo OA templates
  are approved. The sales invoice (SALE-06, MSG-SALE-15) uses them too: `POST /api/sales-invoices/{id}/finish`
  (`{ wantsQrCode }`, once, after the save: with a Yes, TRACE-01 first publishes the lots that are Not published and have all
  their origin information, `GET /api/sales-invoices/{id}/publish-check` shows them beforehand; the QR code only when a lot is
  Published; called again it answers the current
  result and sends nothing) and `POST /api/sales-invoices/{id}/resend` (only after `finish`, not while the latest sending
  is Sending; a Sending row older than 2 minutes reads Not sent); a profile without a trader account gets a row with no
  recipient, and a walk-in customer or a profile without a phone gets a Not sent row with no phone (error code
  `NO_CUSTOMER_PROFILE` / `NO_PHONE_NUMBER`, MSG-SALE-16). The withdrawal of a lot's origin information (TRACE-04, Owner
  only: `GET /api/harvest-lots/{id}/withdraw-preview`, `POST /api/harvest-lots/{id}/withdraw` with `{ reason }`) uses them
  too: one MSG-TRACE-05 per trader of the current invoices of the lot, listing their invoices, kept with the lot as its
  record; a profile without a phone gets a Not sent row (`NO_PHONE_NUMBER`), a walk-in invoice nothing. The notices are
  sent at the same time after the save (the answer waits about one send). The answer lists each notice with its delivery
  status; a failed send undoes nothing and is marked `toCall`; `GET /api/harvest-lots/{id}/withdrawal-notices` reads the
  notices of the latest withdrawal again. There is no resend. Texts and parameters longer than their columns (2000) are
  cut / left out, as for list rows.
- Mandatory types also go out as one SMS (PM decision OMTS-216, 03/11): to the Owners among the recipients and, for the
  water alert and its withdrawal, to the Zone Managers of the zone among them. The rule is data on the type
  (`NotificationSmsRule`, `NotificationTypes.All`). The SMS shares the queue of the push: sent once, only after the save
  commits, never for a failed save, never retried, and not kept as a row. A failed or slow SMS changes nothing; a
  recipient without a verified phone number gets none and a log line. The text is the MSG text without Vietnamese marks,
  at most 160 GSM 7-bit characters; a money type (`MoneyToOwner`) carries only its name, document code and amount, so
  never a bank account number. The SMS of a list of confirmations and cancellations (`CUSTOMER_PAYMENT_TRANSACTION`,
  `MoneyToOwnerSplit`, PM answer of 12/11/2026) shows the two totals apart, "Customer payment transaction: thu 5,000,000 VND,
  huy 300,000 VND." (a part of 0 is left out); the `amount` of the push and the list stays their sum. Operational texts read as their fields do, digits included. At 160 the values, allowed
  ranges, time, expiry date and, for a new purchase request, kg and pickup date stay; in order the free text gives way to
  a short minimum (zone name or list, raft code or license type name, license number; note, customer, product), then the
  closing sentence, then the longer wording of the water alert, then the free text below its minimum. Logs hold the type
  and the account id only, never what a provider answered.

### Public provenance page

`GET /api/public/provenance/{qrToken}?lang=vi|en` (ORIGIN-01, SCR-ORIGIN-01) is what the web page `/origin/{qrToken}` of the
invoice QR code loads. It needs no login and a logged-in user gets the same answer (BR-ORIGIN-01); it is limited per client IP
(`RateLimits:ProvenancePagesPerMinute`, 429 `TOO_MANY_REQUESTS`) and sent with `Cache-Control: no-store`, so each opening shows the
current data (BR-TRACE-05: a TRACE-03 correction or a new publish shows at once). The token is the 128-bit random base64url code
SALE-05 gave the invoice (22 characters); only the exact code opens the page.

- `lang`: `vi` by default (BR-ORIGIN-02, anything but `en` is Vietnamese). The messages (`message`) and the cooperative
  introduction follow it (the Vietnamese introduction when the English one is empty); every message also carries `messageCode` and
  `messageParams` (atomic values: `cooperative_short_name`, `lot_code`, `withdrawal_date`, `cooperative_phone`). 
- 200 `{ language, status, message, messageCode, messageParams, invoice: { code, date } | null, cooperative, lots[] }`:
  - `status` `Ok`: `lots` holds the lots of the invoice that are Published or Withdrawn, in line order, each `{ lotCode, products
    (names sold from the lot on this invoice), originState, withdrawalDate, message, messageCode, messageParams, origin }`. A
    Published lot has `origin { harvestDate, raftCode, zoneName, map { latitude, longitude, source: Raft | Zone } | null,
    seedings[{ seedingDate, seedItemName }], daysOfFarming, harvestPhotoUrls[], raftPhotoUrls[] }` (signed URLs, 1 hour); a
    Withdrawn lot has `withdrawalDate` and MSG-ORIGIN-01, never the reason, and `origin: null`. Not published lots are not listed.
  - `NoLots`: no lot is Published or Withdrawn, MSG-ORIGIN-02. `Cancelled`: the cooperative block only (`invoice: null`), MSG-ORIGIN-04.
  - `cooperative` is the FARM-01 public block (BR-FARM-09): `fullName, shortName, yearEstablished, officeAddress, phoneNumber, email,
    website, socialLink, logoUrl, coverPhotoUrl, introduction, certifications[{ name, number, issuer, expiryDate, state, fileUrl }]`
    (not Expired).
- 404 MSG-ORIGIN-03 (in `lang`) for a code no invoice has, also for a link that is no code at all.
- Never in the answer (BR-ORIGIN-04, NFR-03; a test fixes the exact set of properties): the customer, prices, amounts, payments,
  debts, any kg, returns, claims, suppliers, seed lot or receipt codes, the tax code, the bank account, the withdrawal reason or any
  internal id. 

### Sales invoices

SALE-09 (`SALES_INVOICES`, zones of the caller's pairs, BR-SALE-08): the Owner, the Zone Manager of the zone and the Accountant view; the three also print and export (the Accountant holds Export on `SALES_INVOICES` since the PM answer of 12/11/2026, OMTS-224 [5.1]). Any other zone answers 403 MSG-GEN-08.

- `GET /api/sales-invoices`: newest first, paged (`page`, `pageSize` up to 100). Filters: `from`/`to` (Vietnam dates, both included; today when neither is given, only `to` is that one day, only `from` runs to today, at most 5 years, else MSG-GEN-01), `zoneId`, `customerId`, `harvestLotId`, `raftId` (a line of a lot of the raft), `statusId`, `code` (part of the code, `%`, `_`, `[` are plain text), `orderCode` (part of the code DH of the purchase request the invoice fulfils, read like `code`; a sale without a request never matches; each row has `orderCode`, null without a request), `paid` (true/false; a Cancelled invoice matches neither) and `cashCollectedByMe=true` (cash this caller collected that is Awaiting confirmation; each row then has `cashAwaitingConfirmation`). No match: 200 with an empty page and MSG-GEN-10 in `message`. A row has `code`, `invoicedAt`, `zone`, `customer` (null for a walk-in), `total`, `status`, `paymentTerm`, `isPaid` (null for a Cancelled invoice), `unpaidAmount` and `deliveryStatus`. `completedTotal` is the total of every Completed match, not of the page; `isPaid` comes from the debt allocation (BR-DEBT-05), a walk-in invoice from its own payments.
- `GET /api/sales-invoices/{id}`: the invoice with `lines`, `payments`, `sendings`, `priceCuts`, `debtEffect` (`isPaid`, `covered`, `unpaid`, `countsInCustomerDebt`, `customerDebt`) and `allowedActions` (SALE-09.1 step 3). The same fields are in the answers of `POST` create, `finish` and `resend`.
- `GET /api/sales-invoices/export` (the filters above, every page) and `GET /api/sales-invoices/{id}/print`: an `.xlsx` file for download and print, Export right in the zone of the invoice (403 MSG-GEN-08 elsewhere). Text is never a formula. An empty list is a header and a zero total. The list file holds at most 5,000 invoices (the list sheet has an `Order` column): a larger selection is 400 MSG-GEN-01 naming the field `Filters` (narrow the period or the filters) and nothing is written; at most 5,001 matches are read. The limit counts the matches before the `paid` filter narrows them.

`allowedActions`, on the rules of the SRS:

| Value | Offered to | When | Endpoint today |
|---|---|---|---|
| `Resend` | Zone Manager of the zone, Owner | not Cancelled, the sale is finished, the latest sending is not Sending | `POST /api/sales-invoices/{id}/resend` |
| `CreateReturn` | Zone Manager of the zone, Owner | not Cancelled, no sales return that is not cancelled | `POST /api/sales-invoices/{id}/sales-return` |
| `CancelReturn` | Owner | a sales return that is not cancelled | `POST /api/sales-returns/{id}/cancel` |
| `Edit` | `SALES_INVOICES` Edit in the zone (the Owner; no other role has it until it is given) | Completed, no sales return that is not cancelled | `PUT /api/sales-invoices/{id}` |
| `Correct` | `SALES_INVOICES` Edit in the zone (the Owner) | Completed, no sales return that is not cancelled | `PUT /api/sales-invoices/{id}/correction` (a price cut only) |
| `Cancel` | `SALES_INVOICES` Delete in the zone (the Owner) | Completed, no sales return that is not cancelled | `POST /api/sales-invoices/{id}/cancel` |
| `Print` | Whoever has Export in the zone | always | `GET /api/sales-invoices/{id}/print` |

The endpoints refuse exactly what this table does not offer (`SalesInvoiceAllowedActions.ChangeRefusal`): 403 MSG-GEN-08, 409 MSG-GEN-09 for a Cancelled invoice, 409 MSG-SALE-20 with a sales return. The detail also has `corrections` (kind, date, reason, total before and after, from the audit log) and `lastCorrectionKind`, `lastCorrectedAt`, `lastCorrectionReason`; a Cancelled invoice has `cancelledAt` and `cancelReason`. The statement of a customer shows the corrections, price cuts and cancellation under each invoice (BR-DEBT-04).

Every sales invoice is made from an order (DH, see Sales orders below). Who may change one is a permission of the zone of the invoice, not a role: `SALES_INVOICES` Edit for an edit and a price cut, `SALES_INVOICES` Delete for a cancellation. Only the Owner holds them (the Zone Manager has View, Create and Export) until the cooperative gives them to another role.

- `GET /api/sales-orders/{id}/invoice-draft` ("Xử lý đặt hàng", `SALES_INVOICES` Create in some zone, 409 MSG-ORDER-05 when the order is not Draft or Delivering): the invoice the order proposes; `zone` is the zone of the first lot it proposes, null without one. `lines[]` are the order lines with `orderedQuantity`, `deliveredQuantity`, `quantity` (what is still to deliver, never below 0; a line already delivered in full stays with 0 so it can be added to), the price and unit of the order line, and `suggestedLotId` (a lot recorded from a harvest plan that serves the order first, then another open lot of any zone the caller may invoice in with the product; `GET /api/harvest-lots/for-sale?salesOrderId=` lists the lots with the same order). Also `subtotal`, `orderDiscount`, `discountShare` (the order discount in proportion to the goods of this invoice, rounded half up, never more than the earlier invoices left of it), `advanceLeft`, `advanceUse` and `nextCode`. Nothing is saved.
- `POST /api/sales-invoices`: `salesOrderId` is required (400 field `Sales order`; the old request link is refused for a new invoice). The customer is the order's: `customerId` and `walkInCustomer` may be sent but must match (400 MSG-ORDER-06). The order has no zone: the invoice takes the zone of its first lot and a `zoneId` sent is ignored. A line names `harvestLotId`, `kg` (always weighed: the stock ledger counts kg), `salesOrderLineId?`, `unitId?` (the kg unit of the product when left out) and `quantity` (required for a unit other than kg; for kg it is the kg). The price is that of the order line (the unit sale price for a line outside the order); a Zone Manager who enters another price is asked MSG-SALE-10, the Owner is not. The money of a line is `quantity × unitPrice` in its unit, rounded half up; the kg ledger takes the kg and, for a unit other than kg, the amount over the kg as its price. `code?` (1–30 of `A-Za-z0-9._-`, unique regardless of case, at most 25 with VietQR), `note?`. The discount is entered (the draft proposes the share of the order discount): a Zone Manager is not asked about a discount that is within that share. The advance the customer paid on the order pays first (parts set against the invoice in the same save), then the cash, Pay later and VietQR rules apply to what is left. 201 adds `order` (`id`, `code`, `statusId`, `status`, `canFinish`) and `orderCanFinish`: the order is open with lines not fully delivered, so the web asks "Kết thúc đơn đặt hàng?" and calls `POST /api/sales-orders/{id}/finish`. The first invoice moves a Draft order to Delivering; the order is Completed by itself when every line is delivered in full. A VietQR sale carries the order in its snapshot; when the transfer arrives it is saved whatever the order's status has become (the audit log notes an order that was ended meanwhile).
- `PUT /api/sales-invoices/{id}` (Edit; the `rowVersion` read is required, a stale one answers 409 MSG06): changes a Completed invoice like a purchase receipt: `code?`, `lines` (all of them, same fields as above), `notSoldWholeLotIds`, `discount`, `otherCharges`, `note` (left out, kept). The customer and the zone stay. The debt moves by the difference of the totals (it is never stored). A line that changed is posted again on the product ledger (the old rows reversed), an unchanged line writes nothing; a lot's "sold the whole lot" tick closes or reopens it only when the tick changed. The invoice cannot hold more of the advance of its order than it now needs: its newest parts are cut, the earlier advance is kept. The order's delivered quantities are read again (it completes when everything is delivered; it never reopens because an invoice got smaller). A total under what was paid leaves a debt below zero: 409 MSG-SALE-17 until `confirmOwedBack` is sent (the advance returned to its open order is held, not owed back). The code cannot change once a VietQR transfer paid the invoice (400 `Code`); a taken code answers 409 MSG-GEN-02. Audit: one `SalesInvoice.Corrected` event with the values before and after; the invoice is sent again (`(corrected)`).
- `GET /api/sales-invoices/next-code` offers the next code. The list takes `salesOrderId`; a row and the detail have `salesOrder` `{id, code}` (null for an invoice saved before invoices came from orders); a line has `unit` `{id, code, name, factor}`, `quantity` and `salesOrderLineId`; the detail has `note`.

SALE-07 `PUT /api/sales-invoices/{id}/correction` and SALE-08 `POST /api/sales-invoices/{id}/cancel` (`reason` and the `rowVersion` read are required; a stale version answers 409 MSG06, so two parallel changes apply once). Questions are answered by sending again the field named in `confirmationField`.

- Correction `kind` `PriceCutAfterClaim` only: `priceCutAmount` above 0 and at most the total; the lines stay, and price cuts add up (AC-08). A walk-in invoice given money back needs `customerId` (MSG-SALE-19); a price cut takes `customerId` only then, otherwise MSG-GEN-01. `kind` `DataEntryError` answers 400 with a hint: use `PUT /api/sales-invoices/{id}`.
- A walk-in invoice cannot have its total raised (MSG-SALE-21), also when the same correction attaches a profile. A debt below zero after the change answers MSG-SALE-17 until `confirmOwedBack` is sent, then notifies the Owners and the Accountants (MSG-SALE-18, type `CUSTOMER_MONEY_OWED_BACK`).
- The correction is sent again after its commit (`(corrected)`); before the end of the sale nothing is sent, the end of the sale does.
- Cancel (also taking the advance back): the Sale rows of the product ledger are reversed, the parts of the order's advance set against the invoice are removed (the advance is the order's again and is never cancelled with the invoice), a Completed order is Delivering again, and the other statuses stay. Each cash payment Awaiting confirmation is asked (MSG-SALE-23, `cashGivenBack: [{paymentId, givenBack}]`); only a Yes cancels it. A walk-in invoice with payments kept needs `customerId` (MSG-SALE-19). Only the lots this invoice closed reopen; a request it took in full is no longer Fulfilled, and every figure ignores it from its status. The trader gets MSG-SALE-22 (type `SALES_INVOICE_CANCELLED_MESSAGE`, not a sending of the invoice message) when the profile has a phone; failing to send never undoes the save (GFR-06).

Migration `20261109053538_SalesInvoiceCorrectionAndCancellation` only adds nullable columns.

- `GET /api/origin/{qrToken}` was removed (19/11/2026): it exposed the suppliers, the kg and the water measurements. The QR code of an invoice opens the provenance page `GET /api/public/provenance/{qrToken}` only.
- There is no port weighing slip apart from the lot (decision of 17/11/2026): weighing at the port is the weighing of the harvest lot, cage by cage since 24/11/2026 (see the cage weighing entry below). The trader confirms in person, and an invoice is made from an order (DH), never from a weighing. The `/api/port-weighings` and `/api/me/port-weighings` endpoints, the weighing tables and the `portWeighingId`, `cageKgs`, `cageCount`, `deliveredCages` and `weighedCages` fields were removed.
- `GET /api/me/invoices` (`page`, `pageSize` up to 100, newest first; MSG-GEN-11 in `message` when empty) and `GET /api/me/invoices/{id}`: the logged-in trader's own invoices with their lines, the money, the payments and `qrUrl`, without the staff data. Staff get 403 MSG-GEN-08; an invoice of another profile is 404.



### Sales orders (Đặt hàng, DH)

An order of goods (`DH000001`, the code can be typed, unique regardless of case) is what every sales invoice is made from. Statuses: Phiếu tạm (`Draft` 4401) → Đang giao (`Delivering` 4402) → Hoàn thành (`Completed` 4403) or Đã hủy (`Cancelled` 4404). An order has no zone (`SalesOrders.ZoneId` is null for a new order; old orders keep theirs, unused): the harvest plan decides which zones harvest for it, and `SALES_ORDERS` is cooperative-wide. The Owner saves any order; a Zone Manager (`SALES_ORDERS` View/Create/Edit) takes the buyers at the port (`source` `AtPort`) and changes and finishes every order, but never cancels one (Delete is the Owner's); the Accountant has View/Export and may add an advance (`CUSTOMER_PAYMENTS` Create).

- `GET /api/sales-orders`: paged (`page`, `pageSize` up to 100), newest code first. Filters: `search` (order code, customer code, name or phone), `statusIds` (repeat), `from`/`to` (order day), `pickupFrom`/`pickupTo`, `customerId`, `source`, `createdById`, `receivedById`; `sortBy` `code`|`orderedAt`|`pickupAt`|`total`, `sortDir`. `summary` adds up `total`, `invoiced` and `customerPaid` of the matches that are not cancelled. `GET /api/sales-orders/export` (same filters, Export right) is an `.xlsx`; `GET /api/sales-orders/{id}/print` one order as `.xlsx`.
- `GET /api/sales-orders/{id}` and `GET /api/sales-orders/by-code/{code}`: the order with `lines` (unit, quantity, price, `listPrice`, `deliveredQuantity`), `progress` per unit, `advances`, `invoices` (code, time, total, status, creator; cancelled ones too), `invoicedTotal`, `customerPaid`, `purchaseRequest`, `allowedActions` (`Edit`, `Finish`, `Cancel`, `AddAdvance`, `Process`, `PlanHarvest`, `Print`) and `rowVersion`. `GET /api/sales-orders/next-code` offers the next code without spending it.
- `POST /api/sales-orders` (201): `customerId` or `walkInCustomer: true`, `source` (`App`|`Phone`|`AtPort`), `lines[{productId, unitId, quantity, unitPrice?, priceDecidedByOwner, note?}]` (the unit is one of the units of the product; the price defaults to the list price of the unit), `discount`, `pickupAt`, `receivedById`, `note`, `code?`, `advance?`, `deduplicationKey`. A price other than the list price, or a discount, asked by a Zone Manager answers the 409 question MSG-SALE-10 (send the flag named in `confirmationField` with the same request). A repeated `deduplicationKey` returns the first order. A taken code answers 409 MSG-GEN-02 with `field` `Code`.
- `advance` `{amount, method: Cash|OtherTransfer|VietQr, cashAccountId?, bankReference?, collectedById?, paidAt?, note?}` is saved with the order and is a customer payment that names it: cash is Awaiting confirmation until the Accountant confirms it, a transfer needs its bank reference, VietQr makes a code that waits for the transfer (`advanceQr`). The total of the advances stays at most the total of the order, and a walk-in buyer pays none (400 field `Advance`). `POST /api/sales-orders/{id}/advances` adds one later (same body, 201 with the order, the advance and `qr`); `PUT /api/sales-orders/{id}` takes the same block as a new advance in the same save. The view lists every advance payment under `advances`, whatever its status.
- An advance is reserved for its order while the order is Draft or Delivering: it lowers the customer debt (the customer has paid) but no oldest-first or other allocation uses it for another invoice, and the debt screens list it apart as `reservedAdvances` ("Tiền ứng của DH…"). When the order is Completed or Cancelled what is left is general credit and the normal allocation applies. Money paid back and the limit of a payment above the debt read the debt apart from the reserved advances.
- `POST /api/sales-orders` with `purchaseRequestId` (Owner only, the profile of the request, `source` `App`): the request becomes Ordered (3206) in the same save and its trader is told (MSG-ORDER-04, the channel of the response to a request). `GET /api/sales-orders/request-draft/{requestId}` prefills the form. A request that has its order answers 409 MSG-ORDER-02 with `existingId`; a Declined, Cancelled or Fulfilled one answers 409 MSG-GEN-09; a request accepted before orders existed (not Fulfilled) can be ordered. A request placed by a trader in the Mini App (`POST /api/me/purchase-requests`) makes its own Draft order at once (customer of the request, `source` `App`, one line in the Lồng unit of the product, or in kg at the average kg per cage when it has none, priced at the sale price of the unit, "Người nhận đặt" the first active Owner) and is Ordered in the same save, so the Owner does not enter it again; the answer keeps its shape and adds `salesOrderCode`, and the trader cannot cancel an Ordered request. A product with no unit leaves the request as it was.
- `PUT /api/sales-orders/{id}`: replaces the header and the lines of a Draft or Delivering order (`rowVersion` required; lines of the same product and unit keep their id; an `advance` block adds a new advance in the same save; the answer is the saved order with `advanceQr`, as for a new order). `POST /api/sales-orders/{id}/finish` (Delivering → Completed) and `POST /api/sales-orders/{id}/cancel` (`reason` required, Owner) answer the order and `creditLeft`. Cancelling an order that has an invoice that is not cancelled answers 409 MSG-ORDER-01 (finish it instead).

### Harvest plans (KHT)

The Owner plans a harvest of the rafts of any zones (`KHT000001`, a code the user types is kept; taken in any letter case answers 409 MSG-GEN-02 with `field` `Code`). A plan has a planned time (date and time), lines (`raftId`, `laneNumbers`, `expectedCageCount?`, `note?`; the zone of a line is the zone of its raft, its product that of the raft's cultivation cycle) and may serve any open orders (one plan many orders, one order many plans). A plan has no zone of its own: the view lists `zones` and each line its `zone`. Statuses (owner decision of 24/11/2026, migration `HarvestPlanReceiveHandOverAndPurchaseStaffingWithoutWaiting`): Draft 4201, Assigned 4202 ("Giao QLV", the Zone Managers of every zone of the plan are told, MSG-PLAN-05), Received 4206 ("Đã nhận": a Zone Manager pressed "Nhận", there is no refusal), Harvesting 4203 (the first lot or the first lane left out), HandedOver 4207 ("Đã bàn giao": a Zone Manager pressed "Bàn giao" once every line has its lot or all its lanes left out), Completed 4204 (the Owner alone, only from HandedOver), Cancelled 4205 (from Draft, Assigned or Received while no lot exists). The system never completes a plan by itself: when every line is done the plan stays Harvesting until the hand-over. A Zone Manager sees only the plans handed over that have a line in its zones, and only those lines, and receives, harvests and hands over; it never makes or changes one (`HARVEST_PLANS` View only). Lots and lanes left out are accepted only while the plan is Received or Harvesting (409 MSG-GEN-09 otherwise). `allowedActions` adds `Receive` and `HandOver` (`Finish` is the Owner's "Hoàn thành", only on a HandedOver plan). A plan spanning several zones has one status: one Zone Manager of any zone of the plan receives it for all, and `HandOver` shows (to a Zone Manager that may harvest in a zone of the plan) only when every line of the whole plan, also of other zones, is done.

**Executor of a plan (owner decision of 25/11/2026, migration `HarvestPlanExecutor`).** The plan is still made from the orders by the Owner. When he assigns it (`POST /api/harvest-plans/{id}/assign`, body `{ "rowVersion"?, "executor"? }`) he picks who harvests it: `ZoneManager` (the default, also for a body `{}` and for every plan made before the choice; the flow above) or `Owner`. Any other value is 400 MSG-GEN-01 `Executor` and nothing changes. The plan view (`HarvestPlanView`) and the list item carry `executor` (`"ZoneManager"` or `"Owner"`; a Draft shows `ZoneManager` until it is assigned); the choice is audited (`HarvestPlan.ExecutorSet`) and cannot be changed after the assignment. A plan the Owner harvests himself goes **Assigned (4202), Harvesting (4203), Completed (4204)**: there is no Received (4206) and no HandedOver (4207). Status ids are unchanged and the transition table of old no longer exists, so the code of `HarvestPlanRules` is the only place of the moves. For an Owner plan:
- `allowedActions` (for the Owner): `Edit`, `Cancel` (while no lot), `Harvest` and `SkipLane` from **Assigned** on (Assigned is ready; `POST /api/rafts/{raftId}/harvest-lots` with `harvestPlanLineId` and `skip-lanes` / `unskip-lanes` accept Assigned and Harvesting), and `Finish` ("Hoàn thành") from **Harvesting** once every line has its lot or all its lanes left out (`POST .../finish`, 409 MSG-GEN-09 before that). `Receive` and `HandOver` never appear and `POST .../receive`, `.../hand-over` answer 409 MSG-GEN-09. The first lot or lane left out moves Assigned to Harvesting; the system still never completes a plan; a Completed plan whose line is opened again (lot removed, lane put back) becomes Harvesting again.
- Zone Managers are **not notified** (no `HARVEST_PLAN_ASSIGNED`, also when a later edit adds a zone) and **cannot see or act** on it: the plan is missing (404) from `GET /api/harvest-plans`, `/{id}`, `/by-code`, `/assigned`, the staffing routes and the lane routes, and recording a lot from one of its lines with a Zone Manager token is 400 MSG-GEN-01 `Harvest plan line`. The Owner sees it in `/assigned` with its lines.

- `GET /api/harvest-plans` (filters `search` = plan, raft or order code, `statusIds`, `from`/`to`, `zoneId`, `salesOrderId`, paging), `GET /api/harvest-plans/{id}`, `/by-code/{code}`, `/next-code`, `/assigned` (Assigned and Harvesting plans with a line in the zones the caller harvests), `/eligible-rafts?zoneId=` (Owner: the rafts In Use with an Open cycle and a harvestable lane, with zone, product and lanes `Free`, `InAnotherPlan` or `Harvested`), `/order-draft/{orderId}` (Owner: pickup time and products of an order, with the cages worked out from a Lồng unit).
- `POST /api/harvest-plans`, `PUT /api/harvest-plans/{id}` (`rowVersion`, lines carry their `id`; allowed until the plan is HandedOver), `POST .../assign`, `.../receive` and `.../hand-over` (`HARVESTS` Create in a zone of the plan: a Zone Manager, the Owner too; body `{}` or `{ "rowVersion" }`; 404 for a plan with no line in the caller's zones, 409 MSG-GEN-09 for another status, and for `hand-over` also while a line is still open), `.../finish` (the Owner alone, `HandedOver` only, 403 for anyone else), `.../cancel` (`reason`). All answer `{ plan, message, messageCode }`. `hand-over` tells the Owners (type `HARVEST_PLAN_HANDED_OVER`). A lane may wait in one open plan only (409 MSG-PLAN-02); after the assignment a line with its lot or a lane left out must come back unchanged (409 MSG-PLAN-03); a plan that has started (Harvesting or HandedOver) cannot be cancelled (409 MSG-PLAN-04). The crew of a line is chosen while the plan is Draft, Assigned, Received or Harvesting.
- `POST /api/harvest-plans/{id}/lines/{lineId}/skip-lanes` (`laneNumbers`, `reason`; "Không thu", the Owners are told, MSG-PLAN-06) and `.../unskip-lanes` while the line has no lot.
- Nhân công of a plan (PM decision of 17/11/2026). `GET /api/harvest-plans/{id}/staffing` (`HARVEST_PLANS` View) answers `{ planId, planCode, rowVersion, canEdit, lines: [{ lineId, raftId, raftCode, zoneId, zoneCode, laneNumbers, plannedDate, canEdit, employees: [{ employeeId, code, name, assignmentId, checkedIn }] }] }` for the lines the caller sees. `PUT /api/harvest-plans/{id}/lines/{lineId}/staffing` `{ employeeIds, moveExisting?, rowVersion? }` (`WORK_ASSIGNMENTS` Create in the zone) sets the whole crew of the line in one save and answers the line: each new employee gets a work assignment (job type `Thu hoạch`, the raft of the line, the day of the plan, first period of the day) by the rules of `POST /api/workforce/assignments` (an employee on another raft that day: 409 MSG-LABOR-06 unless `moveExisting`), each employee left out loses the assignment, and one already checked in cannot be taken off (409 MSG-LABOR-07). The Owner chooses while the plan is Draft or Assigned, the Zone Manager of the zone of the line (and the Owner) while it is Assigned or Harvesting; 409 MSG-GEN-09 once it is Completed or Cancelled, after its day (`plannedDate` before today) or when no job type `Thu hoạch` is In Use. The assignments carry `harvestPlanLineId`; `GET /api/rafts/{raftId}/crew-suggestions` reads them. A plan that moves to another day moves the assignments of its lines with it (an employee who checked in stays on the old day, without the link; 409 MSG-GEN-09 when the new day has passed, is in a closed pay period or the employee has other work that day). A line taken out of the plan or moved to another raft, and a cancelled plan, remove the assignments of today or later that no one checked in on and keep the others as a record. Each change is audited as `HarvestPlan.StaffingChanged` besides the `WorkAssignment` events.
- `POST /api/rafts/{raftId}/harvest-lots` takes the form field `harvestPlanLineId`: the lanes are then those of the line without the lanes left out (`laneNumbers` is ignored), the lot carries `harvestPlan {id, code}` and the plan moves on; one line makes one lot (409 MSG-PLAN-03). Removing the lot opens the line again (a HandedOver or Completed plan becomes Harvesting again). A correction keeps to the lanes of the line (409 MSG-PLAN-01). `GET /api/harvest-lots/for-sale?salesOrderId=` lists the lots of the plans that serve the order first (`suggested`), each with `harvestPlanCode`. The order view lists its plans under `harvestPlans`.
- Cage weighing (owner decision of 24/11/2026, OMTS-227): every harvest trip of an order is weighed at the port cage by cage, then the weighing is completed; typing the kg of a lot is gone. `GET /api/harvest-lots/{id}/cages` (`Harvests` View, zone-scoped) answers the weighing sheet `{ lotId, lotCode, statusId, status, rowVersion, harvestedCages, estimatedKg, cages [{ id, number, productId, kg, weighedAt }], products [{ lineNumber, productId, productCode, productName, harvestedCages, weighedCages, cagesKg, weighedKg }], cagesKg, weighedKg, weighedAt, needsCompleting, canEdit }`. `POST /api/harvest-lots/{id}/cages` `{ kg, productId?, clientOperationId? }` weighs one more cage (the next number; `productId` left out is the product of the lot's only line, as a lot holds one; a `clientOperationId` already saved answers the sheet unchanged), `PUT /api/harvest-lots/{id}/cages/{cageId}` `{ kg, productId? }` corrects one and `DELETE /api/harvest-lots/{id}/cages/{cageId}` removes one (its number is not given again); each answers the sheet. They need `HarvestLots` Edit in the zone (the Owner and its Zone Managers) and a lot that is not Closed (409 MSG-GEN-09); `kg` above 0 with at most 3 decimals (400 MSG-GEN-01 `Kg`), a product of the lot (`Product`). A cage saved changes neither the lot's kg nor the ledger: `needsCompleting` tells that the cages differ from the last completed weighing. `POST /api/harvest-lots/{id}/weigh` `{ rowVersion, weighedAt? }` completes the weighing ("Hoàn tất"): the kg of each product line is the sum of its cages (of the lot, the sum of every cage when it has no lines), with the rest of the weighing entry below; 400 MSG-GEN-01 names `Cages` (none weighed), `Line {n} cages` (a line with no cage) and `Cage {number} product` (a cage whose product is no longer on the lot). The body `lines` / `weighedKg` of the typed weighing is gone.
- Trader weighing and agreement (owner decision of 24/11/2026, OMTS-227): the trader of a lot weighs every cage beside the cooperative and agrees to the weighing before the lot is invoiced. The trader of a lot is the one trader account among the customers of the orders (not Cancelled) its harvest plan serves; a lot outside a plan, of walk-in orders only, or of orders of several traders has none and needs no agreement. The weighing sheet (both `GET /api/harvest-lots/{id}/cages` and the trader's) adds `orderCodes`, `traderName` (null: no trader), `traderCages [{ id, number, kg, weighedAt }]`, `traderCagesKg`, `agreement { agreedAt, agreedKg }` (null when none, or when the weighing was completed again since with another kg or time), `needsAgreement` and `canAgree` (trader only: completed, nothing waiting for completing again, not agreed, lot not Closed); the two cage lists are compared by position. Trader routes (`[RequireTrader]`, another lot answers 404): `GET /api/me/weighings?page&pageSize` (`Paged` of `{ lotId, lotCode, harvestedAt, orderCodes, productName, cageCount, cagesKg, traderCageCount, traderCagesKg, weighedKg, weighedAt, statusId, status, agreed, needsAgreement }`, latest harvest first), `GET /api/me/weighings/{lotId}`, `POST /api/me/weighings/{lotId}/cages` `{ kg, clientOperationId? }`, `PUT …/cages/{cageId}` `{ kg }`, `DELETE …/cages/{cageId}` (400 MSG-GEN-01 `Kg`; 409 MSG-GEN-09 once the lot is Closed) and `POST /api/me/weighings/{lotId}/agree` `{ rowVersion }`: the agreement is to the cooperative's weighed kg whatever the trader's cages say; 409 MSG-GEN-09 while not completed or a cage waits for completing again, MSG06 on a stale version, and agreeing again to the same weighing answers the sheet unchanged. Creating an invoice, or adding a lot to one, with a lot whose trader has not agreed answers 409 MSG-GEN-09 with `reasonCode` `WEIGHING_NOT_AGREED` and `lotCode`.
- Harvest lot weighing and close ledger (PM decisions of 17/11/2026). A new lot is `AwaitingWeighing` (2904, "Chờ cân"): not for sale, no rows on the product ledger, no automatic close; it can still be corrected or removed. `POST /api/harvest-lots/{id}/weigh` `{ rowVersion, weighedAt?, lines: [{ lineNumber, weighedKg }], weighedKg? }` (`HarvestLots` Edit in the zone: the Owner and the Zone Managers of it) takes one weighed kg per product line (above 0, at most 3 decimals; a lot with no lines takes `weighedKg`), moves the lot to Open, writes the Harvest rows at the weighed kg, and answers `{ lot, message, messageCode }`; on an Open or Reopened lot it weighs again (the Harvest rows are undone and written again, nothing else on the ledger changes). `weighedAt` defaults to now, is not before the harvest and not in the future. The lot view adds `weighedKg`, `weighedAt`, `weighedByName`, `canWeigh`, `canReopen` and `weighedKg` on each product line; the figures (remaining, difference) count from `weighedKg ?? estimatedKg`, and the days before automatic close from `reopenedOn ?? weighedOn ?? harvestDate`. A weighed lot's product lines cannot be corrected (400 MSG-GEN-01 on `Product lines`): the kg change by weighing again. Every close (by hand, sold whole lot, time limit, sales return) writes a `LotClose` ledger row per product with what is left of it on the lot (none when 0); every reopen undoes those rows. `POST /api/harvest-lots/{id}/reopen` `{ rowVersion, reason }` (same access) reopens a lot closed Unsold, Spoiled, by the time limit or with nothing left; a lot closed by an invoice reopens only through it (409 MSG-GEN-09). `GET /api/harvest-lots` (`Harvests` View, zone-scoped) lists every lot: `from`, `to`, `zoneId`, `raftId`, `productId`, `statusId`, `weighed`, `closeReason`, `hasPlan`, `search` (lot or plan code), `page`, `pageSize`, `sort` (`code` default, `harvestedAt`, `remainingKg`), `sortDir` (`desc` default); it answers `{ items, total, page, pageSize, totals }` with `products`, `figures`, `harvestPlan` and `recordedByName` on each item. `GET /api/harvest-lots/{id}/movements` lists the ledger rows of the lot, oldest first (kinds Harvest, Sale, Disposal, Return, Reversal, LotClose); a Sale row has `salesOrderCode` (the order of its invoice). The lot view adds `references { seedLots [{ receiptId, receiptCode, lineNumber, seedLotCode, itemCode, itemName }], nurseryBatch {id, code}?, salesOrders [{id, code}] (the orders its harvest plan serves), closedByInvoice {id, code}? }` and `productStocks [{ productId, productCode, productName, harvestedKg, soldKg, remainingKg, returnedKg }]` (the ledger of the lot per product, in line order). The migration treats every lot there is as weighed (`weighedKg = estimatedKg`) and writes LotClose rows for the Closed lots that still have kg on the ledger.
- A purchase request from the Mini App makes its Draft order at once and stays `AwaitingResponse` (the Owner still accepts or declines it); the trader cancelling it cancels the Draft order too. A request for a product whose unit has no price is refused with 400 MSG-GEN-01 on `Product`. A cancelled sales invoice sends its order back to Draft when no other invoice of the order stands, else to Delivering (from Completed).

### Employee scope of a Zone Manager (owner decision of 24/11/2026)

An account whose access is limited to some zones (a Zone Manager, or an Accountant granted a zone) sees only the employees who have at least one working zone among the zones of its `LABOR_WORKERS` View grants; the Owner and a caller granted all zones see everyone. The filter is in the query (`EmployeeSearchFilter.VisibleZoneIds`), not after loading: `GET /api/employees` (list, search, `total`, export), `GET /api/employees/{id}` (404 for an employee out of scope; `PUT`, `end-employment`, `resume`, `working-zones` answer 404 too), `GET /api/workforce/leave-requests` (only the requests of employees working in the caller's `WORK_ASSIGNMENTS` zones), and the employees offered to staff a purchase request (`GET /api/purchase-requests/staffing-employees`, `GET /api/work-plans/{id}/employees`; choosing one outside the scope answers 400 field `Employee`). `GET /api/workforce/lookups` and the staff attendance list already followed the working zones. An employee of several zones is seen through the zone in scope, and the view shows only the working zones the caller sees (`workingZones`); the Owner sees all. A Zone Manager can no longer add its zone to an employee it does not see (`POST /api/employees/{id}/working-zones`: 404): the Owner, or a caller granted all zones, brings an employee into a zone. Not changed: the crew validation of a nursery, seeding or harvest record (`Crew` field) still accepts any employee of the cooperative who still works here; the screens offer only the in-scope ones.

### Purchasing (Mua hàng) of a Zone Manager (owner decision of 25/11/2026)

A Zone Manager does in Mua hàng what the Owner does, only in the zones of its pairs. This reverses the 20/11/2026 reading that receipts follow the role only (BR-FARM-08): the function `PURCHASE_RECEIPTS` stays `CooperativeWide` in the catalogue (`GET /api/permission-functions`, `/api/me/permissions`: a Zone Manager still lists it with the actions View, Create, Edit, Delete, Export), but the record rule of `PurchaseReceiptAccess` decides which receipt a caller sees and writes.
- **Zone of a receipt.** `PurchaseReceipt.ZoneId` (optional for the Owner, **required for a Zone Manager**) is the zone the goods are for; a return takes the zone of its receipt (`PurchaseReturn.ZoneId`, or the `zoneId` sent for a return with no receipt). Receipts and returns with no zone are the Owner's and the Accountant's alone.
- **Who sees what.** The Owner, an Accountant (any pair: the role views and exports but cannot record) and a pair at All zones see every receipt and return, those with no zone included. A Zone Manager sees only those whose zone is a zone of one of its Zone Manager pairs: `GET /api/purchase-receipts` (and `/export`, `/raft-material-costs`) and `GET /api/purchase-returns` (and `/export`) are filtered in the query (`zoneIds` asked for are limited to its zones, so asking for another zone lists nothing), and every route addressed by id answers **404** for a receipt or return outside its scope: `GET/PUT/DELETE /api/purchase-receipts/{id}`, `.../payments`, `.../copy`, `.../complete`, `.../cancel`, `.../cancel-preview`, `.../returns` (list and create), `.../price-setup`, `GET/PUT/DELETE /api/purchase-returns/{id}`, `.../cancel`.
- **Who writes what.** A Zone Manager records (`POST /api/purchase-receipts`), edits, completes, removes and cancels receipts, records returns (from a receipt or alone) and sets prices (`PUT /api/purchase-receipts/{id}/price-setup`, which was Owner-only and is now `PURCHASE_RECEIPTS` Edit, the zone checked) only with a zone of a pair that holds the action: 403 MSG-GEN-08 for another zone, 400 MSG-GEN-01 `Zone` when it names none; moving a receipt to a zone outside its pairs is refused the same way. The earlier rules stay: a Draft or a Completed receipt is changed or cancelled by who recorded it or by the Owner, and a Zone Manager still confirms the Owner's approval for a return (`ownerApproved`). The rafts a line may name are still those of the zones of its Zone Manager pairs.
- **Suppliers.** `ZONE_MANAGER` now holds `SUPPLIERS` View only (was View, Create, Edit, Export): it reads the supplier list and detail to pick one on a receipt, and `POST/PUT/DELETE /api/suppliers` and the supplier file import are 403. Supplier payments and debts (`SUPPLIER_PAYMENTS`) stay with the Accountant and the Owner: 403 for a Zone Manager. The Accountant is unchanged (`SUPPLIERS` View and Export).
- **Invoice file or photo.** The receipt module has no attachment (no invoice photo field on a receipt in this API), so nothing was scoped for it.
- **Supplier screens.** For a Zone Manager `GET /api/suppliers/{id}/history` and the `receipts` of `GET /api/suppliers/{id}` hold only the receipts and returns of its zones (none with no zone); `totalPurchased` of the supplier list and detail is **null** (it sums the receipts of every zone, so only the Owner and the Accountant read it; sorting or filtering by it is 403); `GET /api/purchase-receipts/raft-lookup` lists only the rafts of its zones.
- Not covered: the lists of seed lots and of input stock (`INPUT_ITEMS`, seed lot register) read receipts through their own function codes and are unchanged; the purchase notification to the Accountant and Owners is unchanged.

### Seasonal workers and the EMPLOYEE role (owner decision of 25/11/2026)

Two employee types stay: `Staff` (official employee, fixed monthly salary, working zone(s) chosen at creation and changeable) and `SeasonalWorker` (called when there is work, paid by hour or shift, **no fixed zone**: today zone A, tomorrow zone B by assignment). Pay calculation is untouched.

- **Zones.** `workingZoneIds` is required for a `Staff` employee (400 MSG-GEN-01 `Working zones` when empty, on create, and on an edit that leaves a Staff employee with none) and optional for a `SeasonalWorker` (create and edit with `[]`). An edit that turns a seasonal worker into Staff needs a zone. Assigning work (`POST /api/workforce/assignments`, harvest plan staffing, purchase-request staffing) no longer adds the zone of the work to the working zones of a seasonal worker (LABOR-02 step 9 stays for Staff). Assignment, attendance by assignment (check-in/out) and crews never required a working zone for a seasonal worker.
- **Visibility.** `EmployeeCaller` / `EmployeeZoneScope`: a caller limited to some zones sees (and, with `LABOR_WORKERS` Edit in some zone, maintains) the employees working in its zones **and every seasonal worker**: the list and export (`EmployeeSearchFilter.IncludeSeasonal`), the detail (no 404), the lookups of `GET /api/workforce/lookups`, the leave-request list, the staffing pickers of purchase requests and work-day plans. A caller that sees no zone at all still sees nobody. Reviewing a leave request and correcting attendance of an employee with no working zone is allowed to any holder of `ATTENDANCE` Edit in some zone. Zone data stays zone-filtered as before.
- **Account of every seasonal worker.** `POST /api/employees` (Owner, or a Zone Manager with `ownerApproved`) with `employeeType: SeasonalWorker` and a `phoneNumber` creates a Staff login account in the same save (`SeasonalAccountProvisioner`): display name and phone of the profile, username `nv000012` (the lower-case employee code; else `tv` + the phone digits; or the `account.username` sent by the Owner), status Active, `MustChangePassword` true, grant of the fixed role `EMPLOYEE` at All zones (`GrantedByAccountId` = the person who saved). The initial password is the constant `SeasonalWorkerAccounts.InitialPassword` (`OMTS@1234`, meets `PasswordPolicy`) and it never expires (`TemporaryPasswordIssuedAt` 2999-12-31, as the demo accounts), so a worker called weeks later can still sign in; it must still be changed at the first login (same gates as a temporary password: phone verification first, then `PASSWORD_CHANGE_REQUIRED`; the new password cannot be the initial one). The `EmployeeSaved` answer carries `temporaryPassword` (= the fixed password), `temporaryPasswordExpiresAt` (null), `accountMessageCode`, **`fixedPassword`** (true) and `employee.account`. An `account` block with `grants` is the Owner's own choice and keeps the staff flow (random one-time password, those grants, no EMPLOYEE role). A seasonal worker with no phone number is saved without an account and gets one on the edit that adds a phone number (the answer then carries the password fields). When the phone number or username is another account's the profile is still saved with no account and **`accountNote`** = `MSG-ACCOUNT-09` (a phone number may be shared with another kind of account, BR-LABOR-01); an edit never fails for it. `POST /api/employees/{id}/account` for a seasonal worker without grants does the same (fixed password, EMPLOYEE role); with `grants` it is the staff flow. The file import (`POST /api/employees/import`) creates the account of each seasonal row that has a phone number and skips the others.
- **Reset.** `POST /api/accounts/{id}/reset-password` of an account linked to a seasonal worker (type in force today) sets the fixed password again (must change at first login, never expires, every session ends) and answers `temporaryPassword` = `OMTS@1234`, `temporaryPasswordExpiresAt` null, `fixedPassword` true; any other account keeps a random 10-character password for 24 hours (`fixedPassword` false). `StaffAccountWithPassword.temporaryPasswordExpiresAt` is now nullable.
- **Role `EMPLOYEE`.** A fixed role (`FixedRoles.Employee`, `StaffRole.Employee`, `GET /api/roles`, `GET /api/accounts/{id}/grants`) with **no function of the matrix** (empty permission list): it is the Effective pair that lets the account sign in, never zone-bound (All zones, `ScopeType` AllZones). `GET /api/me/permissions` of a seasonal account lists the grant `roleCode` `EMPLOYEE` and no function. Its notification role is `Employee`. The Owner may also grant it by hand. The own work of the employee (`/api/me/workforce`, `/api/me/attendance-qr`, `/api/me/leave-requests`) still takes the employee-app token (phone login code, LABOR-03 and LABOR-06): a staff-token route for these endpoints is **not** added (it would change the token isolation of BR-LABOR-02); asked of the owner.
- **Seed step.** `dotnet OMTS.Api.dll seed --bootstrap` runs `SeasonalAccountBackfill` after the default roles: it creates the fixed-password EMPLOYEE account of every Employed seasonal worker (type in force today) that has no account, once (idempotent); a worker with no phone number, whose phone number is another account's, or with no free username is skipped and logged; it never fails the seed. With `--demo` on a shared server (not Development) the step is skipped and the accounts the demo seeder creates for its seasonal workers get the demo secret like every other demo account (no fixed well-known password on a shared server); in Development they keep `OMTS@1234`. No schema change: the `EMPLOYEE` role row is created by the existing `DefaultRoleSeeder` (readiness `HasDefaultRoles` now expects three rows).

### Purchase request staffing without waiting (owner decision of 24/11/2026)

`POST /api/purchase-requests/{id}/staffing` (body `PlanPurchaseStaffing`, unchanged; called by the person responsible of the work-day plan, or `POST /api/work-plans/{id}/assignments`) now makes a work assignment at once for each chosen employee (job type, raft and day of the work-day plan; the zone of the raft is added to the employee's working zones, as in LABOR-02 step 9) and tells the employee with a plain notice (type `PURCHASE_SHIFT_ASSIGNED`); the employee has nothing to accept or refuse. The request stays Awaiting response (3202, `AwaitingResponse`): it does not enter Awaiting staff (3205), which only requests sent before this change may still have. Each chosen employee also gets a `PurchaseShiftInvitation` row that is already answered (`approved: true`, `reservedWorkDate` the day) so `GET /api/purchase-requests/{id}/staffing`, `staffing` of the request view (`approvedEmployees`, `hasEnoughStaff`...) and `GET /api/me/purchase-shifts` keep their shape; `canRespond` is always false for them and `POST /api/me/purchase-shifts/{id}/response` answers 400 for a request that is not Awaiting staff. `POST /api/purchase-requests/{id}/accept` needs only a staffing plan (cages, day, pickup and at least one assigned employee, plus the approved work plan of an active responsible): 400 `Staffing plan required` without one, no counting of agreements; accepting makes no second assignment. Declining or the trader cancelling releases the assignments that no one has checked in on (LABOR-02.2), as before. Choosing more employees later is still allowed; the cages, day, pickup and capacity of a sent plan cannot change (400 `Staffing plan already sent`). An employee already assigned that day, on leave or without an active staff account is refused (400 `Employee unavailable`).

### Quality claims (CLAIM-01, CLAIM-02)

A trader reports a quality problem on one of their invoices with photos; the Cooperative Owner responds, and when the response is an offer the trader accepts or declines it (PM answer 12/11: the Owner proposes, the trader chooses). A claim changes neither the invoice nor the customer debt (BR-CLAIM-01/02).

- `POST /api/me/sales-invoices/{id}/quality-claims` (trader, multipart: `description` up to 2000 characters, one `photos` part per photo, 1 to 10 JPEG or PNG of at most 10 MB; 201 with the claim, code `KN000123`, and MSG-GEN-05). Only an invoice of the trader's own profile (a profile merged into theirs counts; else 404) that is not cancelled, has no sales return (sales returns come with CLAIM-03: the check is the seam `IInvoiceSalesReturnCheck`) and has no other open claim, Awaiting response or with an offer the trader has not answered (409 MSG-GEN-09, one filtered unique index per invoice). A photo that cannot be uploaded answers 502 MSG-GEN-03 and saves nothing. The Owner gets MSG-CLAIM-01 (not a mandatory type). A finished claim (rejected, accepted or declined) does not stop a new one and OMTS sets no time limit.
- `GET /api/me/quality-claims` (own claims, newest first, MSG-GEN-11 when none) and `GET /api/me/quality-claims/{id}` (404 for another trader's): the description, photo links, status, offer, `offerAmount`, the response note and the `answer` of the trader (note, time). The photos are private (BR-CLAIM-01): signed links for 1 hour, never on the provenance page.
- `GET /api/quality-claims` (`statusId`, `page`, `pageSize`; Awaiting response first) and `GET /api/quality-claims/{id}` (customer with phone number, invoice with lines, lots, kg, amounts, status and price cuts, the SMS sendings, `rowVersion`): QUALITY_CLAIMS View, so the Owner and the Zone Managers, who see the claims of invoices in their zones only (PM default; another zone answers 403). An Accountant has no access.
- `POST /api/quality-claims/{id}/accept` (`{ offer: "ReturnAllGoods" | "PriceCutWholeInvoice", priceCutAmount?, note?, rowVersion }`; the claim becomes **Offer made**, 3704; `priceCutAmount` is its own field, required for a price cut, above 0, at most the invoice total, whole dong, and refused for a return: 400 MSG-GEN-01 `Price cut amount`) and `POST /api/quality-claims/{id}/reject` (`{ note, rowVersion }`): Owner only, while Awaiting response; a response cannot be changed (409 MSG-GEN-09, or MSG06 when the form is stale). Accepting answers 409 MSG-CLAIM-03 when the invoice was cancelled or has a sales return (it can still be rejected). The offer only records what was proposed: the return is CLAIM-03 and the price cut SALE-07 (`PUT /api/sales-invoices/{id}/correction`, PriceCutAfterClaim), which the Owner runs after the trader accepts. Neither changes here. The trader gets a push and an SMS with MSG-CLAIM-02; a message that cannot be sent undoes nothing (GFR-06), it stays in the trader's list and the sending is listed Not sent on the claim; a profile without a phone number gets a Not sent row (`NO_PHONE_NUMBER`).
- POST /api/me/quality-claims/{id}/accept-offer and /decline-offer (trader, { note? } up to 1000 characters): the trader of the claim answers an offer once, while it is Offer made; the claim becomes Accepted (3703) or Offer declined (3705), with nswer on the claim and an audit row (QualityClaim.OfferAccepted / OfferDeclined). 200 with the claim and MSG-GEN-05; 404 for another trader's claim; 409 MSG-GEN-09 when the claim is not waiting for an answer (a second answer sees the status that won). The Owner is not notified of the answer (no message of the SRS for it, see the PM question); migration ClaimOfferMadeAndAnswer moved the claims accepted before to Offer made.


### Sales returns (CLAIM-03)

A sales return takes back all the goods of one invoice (BR-CLAIM-03); there is no partial return. The Zone Manager of the invoice's zone and the Owner record it (`SALES_RETURNS` Create; an Accountant has no access); it is made with or without a claim, and a claim is not linked to it (BR-CLAIM-02). Code `TL000123` (PM answer 12/11: `TH` is the purchase return's).

- `GET /api/sales-invoices/{id}/sales-return/preview` (`?customerId=` the profile chosen for a walk-in invoice): the lines with their lots and statuses, `value` (the invoice total at its latest values), the debt before and after (`debtAfter`), the claims of the invoice, `requiresCustomer`, `ownerMustAgree` and `rowVersion`.
- `POST /api/sales-invoices/{id}/sales-return` `{ reason, rowVersion, lines: [{ invoiceLineId, outcome: "BackForSale" | "ThrownAway" }], customerId?, ownerAgreed?, thrownAwayLots? }` answers 201 `{ return, debtAfter, owedBack, message, messageCode }`. A Zone Manager is asked MSG-CLAIM-04 (409, `confirmationField: ownerAgreed`). A walk-in invoice needs `customerId` (MSG-GEN-01): the invoice and its payments move to that profile first. A lot whose oysters are thrown away and that is still for sale is asked MSG-HARVEST-02 (`thrownAwayLots: [{ harvestLotId, stillForSale, leftoverKg?, closeReason? }]`); a lot that also takes oysters back for sale on the same return is not asked. A Zone Manager who ticks the close of a lot of another zone gets 403 MSG-GEN-08 with the lot named: the whole return is refused and the text says to untick the close or ask the Owner (PM answer 12/11).
- The value takes the invoice out of the customer debt (BR-DEBT-02, shown in the statement of the customer on the return date), the return covers the invoice in the allocation (BR-DEBT-05), and the invoice is locked (SALE-07, SALE-08: 409 MSG-SALE-20). When the debt is now below zero the Owners see it in MSG-CLAIM-05 (type `SALES_RETURN_RECORDED`) and the Accountants get MSG-CLAIM-05 too (type `CUSTOMER_MONEY_OWED_BACK`) with the request to pay the money back (DEBT-02).
- `GET /api/sales-returns/{id}`: the return with its lines and, for lots back for sale, `remainingKg` and `remainingAfterCancelKg` (SALES_RETURNS View in the zone). The invoice detail has `salesReturns` (cancelled ones included).
- `POST /api/sales-returns/{id}/cancel` (Owner only, `{ reason, rowVersion, confirmNegativeRemaining? }`): the return counts nowhere and the invoice can again be corrected or cancelled. MSG-CLAIM-06 is the warning first when the kg that went back for sale were sold since (the remaining kg goes below zero). A return can be cancelled after the customer was paid back: nothing blocks it, the money paid back stays as it is and the debt goes up again (PM answer 12/11). Each lot keeps its status, whatever happened since; nothing reopens or closes. A cancellation sends no notification.
- `GET /api/sales-returns` (SALES_RETURNS View, zone-scoped by the zone of the invoice) lists the returns, cancelled ones included: `from`, `to` (return date), `customerId`, `zoneId`, `statusId`, `search` (code of the return, the invoice or the customer, or the name of the customer), `page`, `pageSize` (1 to 100), `sort` (`code` default, `returnDate`, `value`), `sortDir` (`desc` default). It answers `{ items, total, page, pageSize, totals }`; an item has `id, code, returnDate, recordedAt, invoiceId, invoiceCode, salesOrder {id, code}?, customer {id, code, name}?, zoneCode, value, statusId, status, recordedByName, lineCount, backForSaleKg, discardedKg`; `totals {value, backForSaleKg, discardedKg}` sum the returns that stand among every match. `GET /api/sales-returns/export` is the same list as .xlsx (same filters and zone rule, at most 10 000 rows, a last row with the totals). The return view adds `salesOrder?`, `customer?`, `zoneCode`, `backForSaleKg`, `discardedKg`, `canCancel` (the Owner, and the return stands) and `productCode` on each line. The order view lists its returns under `salesReturns [{ id, code, statusId }]`: a return changes nothing on the order.
- Product ledger: a return writes one `Return` row (+kg, at the price of the invoice line, with the customer) per line taken back for sale, when the lot was split into that product; a line thrown away writes nothing. The lots are reopened first (which undoes their `LotClose` rows), then the Return rows are written, so a lot that was closed holds what its close had taken off plus the kg put back. Cancelling the return undoes its Return rows; a lot that is Closed by then gets its close written again for those kg, so a closed lot still holds none. The migration writes the Return rows of the returns that already stand.
- Lot figures (BR-HARVEST-07): returned, back-for-sale and discarded kg of returns that stand go into `IHarvestLotSalesQuery`, so HARVEST-04 and the remaining kg follow. TRACE-02 lists the returns under each invoice; a lot whose returned oysters were thrown away is left out of its invoice's provenance page; a TRACE-04 notice leaves out invoices taken back by a return (BR-TRACE-07).
- Concurrency: a parallel correction, cancellation, payment or second return touches the same invoice or profile row, so one fails with 409 MSG06 (or MSG-GEN-09 with a filtered unique index on the invoice when the winner is a return); nothing is applied twice. The notification goes out after the commit.

Migration `SalesReturns` is additive: two new tables, the `SALES_RETURN` statuses (4101, 4102) and transition 240.

### Sales reports (REPORT-01 to REPORT-03)

Analyze sales performance: revenue, price cuts, sales returns, net revenue and kg by customer, product, zone or period. `SALES_REPORTS` View (and Export for the file): the Cooperative Owner and the Accountant (§I.4.4); any other caller gets 403 MSG-GEN-08, no token 401. Worked out each time, nothing stored, no migration (BR-REPORT-01, as BR-FARM-10).

- `GET /api/reports/sales-performance?from=&to=&groupBy=&zoneIds=&customerIds=&productIds=` (the three id lists are repeated parameters). Period: the current month by default, at most 5 years; `groupBy` is `customer` (default), `product`, `zone`, `day`, `week` or `month`. Answer: `{ computedAt, from, to, groupBy, filters { zones, customers, products }, groups[], totals, message, messageCode }`; a group has `key`, `label`, `invoiceCount`, `invoicedKg`, `revenue`, `priceCuts`, `salesReturns`, `netRevenue`, `soldKg`, and for the customer grouping `netMoneyReceived`, `receivable`, `owedBack` and `netDebt` (null for the walk-in row and for the other groupings). No invoice in the selection: 200 with empty `groups`, zero `totals` and MSG-GEN-10 in `message`. A start after the end, a longer period, an unknown grouping, zone, customer or product id: 400 MSG-GEN-01 naming every field (`Grouping`, `From date`, `To date`, `Zone`, `Customer`, `Product`). A zone outside the caller's pairs: 403 MSG-GEN-08 (as SALE-09); without `zoneIds` only the zones of the caller's pairs count.
- `GET /api/reports/sales-performance/invoices?...&groupKey=` (the same query and the `key` of a group): `{ from, to, groupBy, groupKey, invoices[{ id, code, date, customer, zone, total, returnValue, net }], message, messageCode }`, newest first; `net` is the total minus the value of the sales return that stands. A key with no invoice: MSG-GEN-10; no key: 400 `Group key`.
- `GET /api/reports/sales-performance/export?...`: `sales-performance-{groupBy}-{from}-{to}.xlsx`, Export right. Sheet `Sales performance` holds the groups and a `Total` row, sheet `Choices` the period, the grouping and the zones, customers and products chosen. An empty selection gives the headers and a zero total row.

Definitions: an invoice is in the period by its invoice date (business date, UTC+7), Cancelled ones never count and a corrected invoice counts at its latest values, in the zones of the caller's pairs by the zone of the invoice. Revenue is the invoice total (already lower by the price cuts, as the daily revenue of FARM-06); price cuts are shown apart from discounts; sales returns are the values of returns that stand (not cancelled), counted in the period of their invoice whatever the date of the cut or the return; net revenue is revenue minus sales returns; invoiced kg and sold kg follow BR-HARVEST-07 (sold = invoiced minus returned). By product, the rows are the line amounts plus rows `discounts`, `other-charges` and `price-cuts` (each with the same part of the sales return of its invoice), so the rows add up to the revenue and the net revenue; a product filter keeps the invoices that have the product on a line. Weeks are ISO weeks (Monday start) and every period `key` is the first day of the bucket (`yyyy-MM-dd`). By customer, walk-in invoices are one row (`walk-in`); `netMoneyReceived` is the Completed payments minus money paid back dated in the period; the customer debt (`CustomerDebtMath`, with discounts and adjustments) of everything dated up to the end of the period, payments Awaiting confirmation included, is split as DEBT-03 does: `receivable` is the debt above 0, `owedBack` what the cooperative owes the customer (a positive amount) and `netDebt` the signed debt, and the totals add each of them (PM answer 12/11: the money owed back is not hidden in the receivable). Money received and paid back with no customer (the payments of walk-in sales, transfers completed with no customer) is one more row, key `unassigned`, so `netMoneyReceived` of the total matches the payments of the period; it is left out when customers are chosen. None of the money figures follows the zone filter, and the customer rows only list customers with an invoice in the selection. REPORT-02 and REPORT-03 reuse `ReportFilterResolver` (right, zones, period, ids), `ReportPeriodBuckets` and `ReportExportFile` under `Features/Reports`.

#### Price trends (REPORT-02)

Same right, zones, period and answers as REPORT-01 (no data: 200 with MSG-GEN-10 in `message`; zone not accessible 403 MSG-GEN-08; unknown ids and wrong period or unit 400 MSG-GEN-01 naming `From date`, `To date`, `Time unit`, `Zone`, `Product`). Read only, no migration (BR-REPORT-02).

- `GET /api/reports/price-trends?from=&to=&unit=&productIds=&zoneIds=` (id lists repeated; `unit` is `day` (default), `week` or `month`; all products by default; the current month by default). Answer: `{ computedAt, from, to, unit, filters { zones, products }, series[{ productId, productName, periods[{ key, label, soldKg, averagePrice, lowestPrice, highestPrice }] }], message, messageCode }`. Series are in product name order, periods in time order; a period with no counted line is not listed. `key` is the first day of the bucket (`yyyy-MM-dd`), `label` the day, the ISO week (`2026-W45`) or the month.
- `GET /api/reports/price-trends/lines?...&productId=&periodKey=` (the same selection, the product and the `key` of a period; any day of the period also names it): `{ from, to, unit, productId, periodKey, lines[{ invoiceId, invoiceCode, date, customer, kg, unitPrice, amount }], message, messageCode }`, newest first; `customer` is null for a walk-in customer. A missing or unknown `productId` is 400 `Product`, a missing or non-date `periodKey` 400 `Period key`; a period without a line is MSG-GEN-10.
- `GET /api/reports/price-trends/export?...`: `price-trends-{unit}-{from}-{to}.xlsx`, Export right. Sheet `Price trends` has one row per product and period (`Product`, `Period`, `Sold kg`, `Average unit price`, `Lowest unit price`, `Highest unit price`), sheet `Choices` the period, the time unit and the zones and products chosen. An empty selection gives the headers only.

Definitions: the lines of the invoices dated in the period (business date UTC+7), in the zone of the invoice, at their latest values (a corrected unit price counts as corrected), without Cancelled invoices and without invoices that have a sales return that stands; an invoice whose return was cancelled counts again (BR-CLAIM-03: "has a sales return" means one that is not cancelled). Because such an invoice is left out whole, the sold kg is simply the line kg. `averagePrice` is the total of the line amounts divided by the total kg (not the mean of the unit prices); `lowestPrice` and `highestPrice` are unit prices of the lines. Prices are VND per kg and are not rounded (an average may have decimals). Discounts, other charges and price cuts are not spread over the lines, so a price cut leaves the figures as they were.

#### Post-sale issues (REPORT-03)

Same right, zones, period and answers as REPORT-01 (400 MSG-GEN-01 fields: `From date`, `To date`, `Grouping`, `Group key`, `Zone`, `Customer`). Read only, no migration (BR-REPORT-03). Unlike REPORT-01, each event is counted by its own date, so an invoice of last month with a price cut, a return or a cancellation this month counts this month.

- `GET /api/reports/post-sale-issues?from=&to=&groupBy=&zoneIds=&customerIds=` (`groupBy` is `customer` (default), `zone` or `lot`). Answer: `{ computedAt, from, to, groupBy, filters { zones, customers }, groups[], totals, message, messageCode }`; a group has `key`, `label`, `claimsSent`, `claimsAwaiting`, `claimsRejected`, `claimsAccepted`, `returnsCount`, `returnedKg`, `discardedKg`, `returnsValue`, `priceCutsCount`, `priceCutsAmount`, `cancelledCount`, `cancelledTotal`. `key` is the customer id (`walk-in` for invoices without a profile), the zone id or the lot id. `totals` has the same figures (key `total`) and counts every event once, whatever the grouping, so by lot it is not the sum of the rows.
- `GET /api/reports/post-sale-issues/items?...&groupKey=` (the same selection and the `key` of a group): `{ from, to, groupBy, groupKey, items[{ type, date, invoiceId, invoiceCode, customer, noteOrReason, amount, status }], message, messageCode }`, newest first. `type` is `claim` (note = the description the trader wrote, `status` awaitingResponse, rejected or accepted, no amount), `return` (reason, value), `priceCut` (reason, amount) or `cancellation` (cancel reason, invoice total). A key with no event: MSG-GEN-10; no key: 400 `Group key`.
- `GET /api/reports/post-sale-issues/export?...`: `post-sale-issues-{groupBy}-{from}-{to}.xlsx`, Export right. Sheet `Post-sale issues` holds the groups and a `Total` row (a figure that is null in the screen is an empty cell), sheet `Choices` the period, the grouping and the zones and customers chosen. An empty selection gives the headers and a zero total row.

Definitions: claims by the day they were sent (business date), with their status (only claims made in the app exist); sales returns that stand (a cancelled return counts nowhere) by return date, with value, returned kg and discarded kg (return lines of outcome thrown away); price cuts by their own date and amount (the `SalesInvoicePriceCut` rows of SALE-07; a correction of a data-entry error is never one, and none is listed: correcting an invoice is an ordinary action with its audit); cancelled invoices by their cancel date, with their total. The zone and the customer are those of the invoice. A claim of an invoice that was later cancelled still counts, but its price cuts do not: a price cut is undone with its invoice (PM answer 12/11). By lot, only the returns are counted, once for each lot they take back, with the returned and discarded kg of the return lines of that lot; the number of claims, price cuts and cancelled invoices of the invoices that sell the lot are shown, while the amounts and the value of a return apply to a whole invoice that may sell several lots, so they are not split and are `null` on lot rows. The events of a lot (`items`) are those of the invoices that sell the lot.

### Operations dashboard (FARM-06)

Only the Cooperative Owner opens it; every other caller gets 403 MSG-GEN-08. Read only: every figure is worked out from the
records at the moment of the call and nothing is stored (BR-FARM-10); `computedAt` is when the computation started.

- `GET /api/dashboard?from=&to=&zoneId=`: 200 `{ computedAt, zoneId, dailyRevenue { from, to, total, invoiceCount }, receivables
  { receivables, owedBack }, openHarvestLots { count }, waterAlerts { count, zones[{ zoneId, code, name, measuredAt }] },
  pendingTasks { total, groups[{ kind, count, oldest[{ id, code, label, since }] }] } }`.
- Filters: `from`/`to` (Vietnam dates, both included; today when neither is given, only `to` is that one day, only `from` runs to
  today, at most 5 years) apply to the daily revenue only. `zoneId` applies to the daily revenue (by the zone of the invoice), the
  open harvest lots and the water alerts. Receivables and pending tasks are always of the whole cooperative. A period that cannot
  be read (`from` after `to`, a start in the future, over 5 years, not a date) or a `zoneId` that is not a zone is 400 MSG-GEN-01
  naming the field.
- Definitions (the same as the screens they link to): `dailyRevenue.total` is the total of the invoices of the period that are not
  Cancelled, at their latest values (SALE-09); `receivables` is the total of the customer debts above zero and `owedBack` the money
  owed back to customers, as a positive amount (BR-DEBT-02, DEBT-03); `openHarvestLots.count` is the lots Open or Reopened now
  (BR-HARVEST-04; list them with `GET /api/harvest-lots/for-sale?zoneId=`); `waterAlerts` are the zones In Use whose latest Active
  measurement is outside its thresholds, one entry per zone however many values are outside (BR-WATER-04, WATER-03).
- Pending tasks, one group per `kind`: `PurchaseRequest` Awaiting response (SALE-03), `QualityClaim` Awaiting response (CLAIM-02),
  `EmployeeRequest` Awaiting review (LABOR-05: leave, missed check-in or check-out; no zone). `count` is every record; `oldest` is the 5 that wait longest (`id` opens the record,
  `code` is null for a record with no code, `since` is when it began to wait). `label` names the record without opening it:
  `{customer} – {cages} cages {product}, pickup dd/MM/yyyy` for a purchase request, `{customer} – invoice {code}` for a quality claim,
  `Payroll dd/MM/yyyy–dd/MM/yyyy` for a payroll, `{employee} – leave dd/MM/yyyy[–dd/MM/yyyy]` or `{employee} – missed check-in|check-out dd/MM/yyyy` for an employee request.

### Work assignment, attendance and employee requests (LABOR-02, LABOR-04, LABOR-05, LABOR-10)

`WorkAssignment` holds one job period of an employee and day (the first one has no start time, each change of job adds one),
`Attendance` the check-in and check-out of one employee and day with their recording method, `EmployeeRequest` a leave or a missed
check-in or check-out. Hours are worked out when shown (`WorkHoursCalculator`, BR-LABOR-05) from the attendance settings in force
on the day; nothing is recorded in a pay period whose payroll is Finalized (`MSG-LABOR-04`, BR-LABOR-13).
Zone Manager (in the zone of the raft) and Owner, except where marked Owner:

- `GET /api/workforce/assignments?day=`, `GET /api/workforce/lookups?day=`; `POST /api/workforce/assignments` (409 `MSG-LABOR-06`
  asks to move an employee, resend with `moveExisting`), `PUT`/`DELETE /api/workforce/assignments/{id}` until the employee is
  checked in (`MSG-LABOR-07`), `POST /api/workforce/crew-job` (Change job for crew, 204).
- Work with no raft (decision of 17/11/2026, OMTS-227): `POST /api/workforce/assignments`, `PUT .../assignments/{id}`, `POST .../crew-job` and `POST .../crew-start` take `zoneId` instead of `raftId` (the nursery is a zone);
  a row of the day board then has `raftId`/`raftCode` null and always `zoneId`. The permission is checked on the zone of the assignment. A correction period (`periods[]`) may carry `zoneId` with no `raftId` for a day that already has that zone.
- `POST /api/workforce/assignments/{id}/check-in` and `/check-out` with `qrCode` (the code of `GET /api/me/attendance-qr`, valid
  60 seconds, else `MSG-LABOR-13`) or null to mark by hand; `POST /api/workforce/crew-start`; Staff employees:
  `GET /api/workforce/staff-attendance`, `POST /api/workforce/staff/{employeeId}/check-in|check-out`.
- Owner, or the Zone Manager of the zone (decision of 20/11/2026, `ATTENDANCE` Edit): `GET`/`PUT /api/workforce/attendance/{employeeId}/{day}` corrects times and job periods with a mandatory reason, for any day not in a pay period whose payroll is
  Finalized (409 `MSG-LABOR-04` for everyone, the Owner too), audited as `Attendance.Corrected` with the values before and after. The zone of the day is that of its job periods (a Staff employee, or a day with none: a
  working zone of the employee); a Zone Manager needs the right in every zone the day touches, and in the zone of any raft it moves an employee onto (403 `MSG-GEN-08` otherwise; an Accountant has no attendance right, 403).
  `POST /api/workforce/leave-requests/{id}/review` approves or rejects (a rejection needs a note): the Owner, or a Zone Manager holding `ATTENDANCE` Edit in a working zone of the employee (a request has no zone of its own); applying a missed time
  needs the right in the zones of the day's job periods and of the raft chosen for a seasonal worker with no work that day. `GET /api/workforce/leave-requests` is also read by the Zone Manager (every request, unchanged).
- Work-day plans (`/api/work-plans`, decision of 20/11/2026): `GET /api/work-plans/responsibles?raftId=`, `POST /api/work-plans` and `PUT /api/work-plans/{id}` need `WORK_ASSIGNMENTS` Create (new) or Edit (change) in the zone of the raft, so the
  Owner and the Zone Manager of that zone (another zone: 403). A Zone Manager names itself as the responsible (`responsibles` offers it only itself; another name: 400 `Plan responsible`), so its plan is approved at once and is its own to see and staff;
  it changes only a plan it is responsible for, and a plan tied to a trader's purchase request (`purchaseRequestId`) stays the Owner's (403).
- The employee linked to the caller's account (`Employee.AccountId`): `GET /api/me/workforce?from=&to=` (at most 62 days),
  `GET /api/me/attendance-qr`, `POST /api/me/leave-requests` (leave, or `type` MissedCheckIn / MissedCheckOut with `day` and
  `requestedTime`), `DELETE /api/me/leave-requests/{id}` (cancel while Awaiting review).



### Employees on nursery, seeding and harvest records (OMTS-227, 17/11/2026)

- Nursery batch (`POST /api/zones/{zoneId}/nursery-batches`, `PUT /api/nursery-batches/{id}`), seeding session (`POST .../seeding-records`, its correction) and harvest lot take the form field `crew`: one field per employee id,
  optional, at most 100, each an Employed employee of the cooperative (else 400 on `Crew`). Absent = the crew is unchanged (none on a new record); `replaceCrew=true` with no `crew` field clears it.
  Every read and save answer has `crew: [{id, code, name}]` on the nursery batch, the seeding record and the harvest lot view. Tables `NurseryRecordEmployees`, `SeedingRecordEmployees`, `HarvestRecordEmployees` hold only the ids.
- Automatic work assignment: after a save that names new employees, the saved answer (`NurseryBatchSaved`, `SeedingRecordSaved`, `HarvestLotSaved`) carries `crewAssignment: { created, skipped: [{ employeeId, reason }] }`
  (absent when no employee was added). It is a step after the save, so a record stays saved whatever happens. Job type by name: nursery `Ươm` in the zone of the batch, seeding `Treo giống` on the raft, harvest `Thu hoạch` on the raft; date = today, only
  when the record is of today. `reason`: `PastDate`, `AlreadyAssigned` (never moved), `NoPermission` (no work-assignment Create in the zone), `JobTypeNotInUse`, `Left`, `Failed` (locked pay period, raft no longer In Use, lost race). No attendance is created.

#### Completing the records with a crew, and the crew's work time (OMTS-227, 17/11/2026)

- Gated completions: `POST /api/nursery-batches/{id}/complete` (day = start date of the batch), `POST /api/cultivation-cycles/{cycleId}/seeding-complete` (day = date of each seeding session of the cycle),
  `POST /api/rafts/{raftId}/lanes/{n}/finish-harvest` and `POST /api/rafts/{raftId}/finish-harvest` (the crews of the lots that include the lanes, day = harvest date of each lot) and `POST /api/harvest-lots/{id}/close`.
- Rule (`RecordCrewAttendance`, one service for all): an employee of the crew who HAS a work assignment (zone or raft) on the day of the record must have `CheckOutAt` set. No assignment that day (back-dated record, skipped when saved) never blocks.
- Refusal: 409 `MSG-LABOR-37` with `reasonCode: "ATTENDANCE_OPEN"`, `confirmFlag: "ownerApproved"` and `employees: [{ id, code, name, checkedIn }]`. Override, Cooperative Owner only (403 for anyone else): resend with `ownerApproved: true` and
  `approvalReason` (1-1000 characters, else 400 on `ApprovalReason`); the audit log gets `Record.CompletedWithOpenAttendance` with the reason and the employees left open. Nursery: form fields; the others: JSON body
  (the single-lane finish takes an optional body; `approvalReason` is apart from the leftover `reason` of the lot close).
- Views: each `crew` member of the nursery batch, seeding record and harvest lot gets `work: { checkedInAt, checkedOutAt, minutes } | null` (the attendance of the record's day; minutes = raw time between check-in and check-out, no break, no rounding, null until the
  check-out) and `attendance`: `NotAssigned` | `NotCheckedIn` | `Working` | `Done`. `GET /api/workforce/lookups` employees get `assignment: { zoneId, raftId, raftCode, jobTypeId, job } | null` (the latest job period of the day).
- Pay: a zone-level assignment is paid like any other: hours come from Attendances x job periods (`WorkHoursCalculator`, no raft needed) at the rate of the job type in force.

### Seed catalogue, seed receipts and seed lots (PM decisions of 17/11/2026)

- One table keeps the farming input items; the Giống and Vật tư screens are two views of it: `GET /api/farming-input-items/search?group=Seed` (or `Material`, `Other`). A **Seed item needs a product** ("Loại hàu bán ra"): create and update answer 400 MSG-GEN-01 on `Product` when `productId` is missing, unknown, deleted or not In Use; the other types ignore `productId`. The item view carries `product { id, code, name }` beside `productId` and `productName`. The item detail also carries `productLocked`: true once the Seed item is on a line of a Completed receipt or in any nursery batch or seeding record; the update then refuses a different product with 400 MSG-GEN-01 on `Product` (hint `The oyster of a seed in use cannot change`), and a Seed item that has no product yet can still be given one. The item import (`POST /api/farming-input-items/import`) rejects Seed rows (the file has no product column): seed items are added one by one. `GET /api/products/{id}` adds `sourceSeeds [{ id, code, name }]`: the Seed items that name the product, by code (empty for a trader).
- **Receipt kind.** A purchase receipt has `kind` `Seed` or `Supply` (Material and Other items), set when it is created and never changed: the `kind` of the create body, else the kind of the first line that has an item, else Supply. Every line must match it, else 400 MSG-GEN-01 on `Lines` with `hint` "A seed receipt holds only seed items" or "A supply receipt holds no seed item" (create, Draft save, complete and the edit of a Completed receipt). A saved Draft or a Completed receipt may send `kind` only as it is stored (else 400 on `Kind`). `GET /api/purchase-receipts?kind=Seed|Supply` filters; the receipt view and the list rows carry `kind` and, for a Completed Seed receipt, `seedState` (the combined state of its seed lots); a line of a Completed Seed receipt carries `seedLot { state, remainingQty }`. The copy of a receipt keeps its kind. The migration gives `Seed` to every receipt with a Seed line and `Supply` to the others; a receipt that mixed both (none in the development data) becomes Seed and keeps its lines (the migration prints their count).
- **Purchase returns** take the kind of their receipt: `GET /api/purchase-returns?kind=Seed|Supply`; the view and the rows carry `kind`. A return without a receipt may not mix seed items with the others (400 MSG-GEN-01 on `Lines`); its kind is that of its items.
- **One cycle, one kind of oyster.** A seeding session whose seed item sells as another product than the cycle is refused: 400 MSG-GEN-01 on `Seed lot` with `hint` "The seed grows another oyster than the cycle" (a seed item with no product never matches a cycle that has one). A cycle with no product takes the one of the seed of its first session. Changing the nursery batch of a cycle (only before its first seeding) to a batch of another product is refused the same way. A correction keeps the lot the session already has.
- **A harvest lot holds one product**, the oyster of its cycle: `productLines` has at most one line (400 MSG-GEN-01 on `Product lines` otherwise), of the product of the cycle (400 MSG-GEN-09 `HARVEST_PRODUCT_MISMATCH` otherwise). When the cycle has a product and the form sends no line, the line is built from `cageCount` and `estimatedKg` (cages times the kg of a lồng of the product when no kg are sent); a correction of a lot that has its line does the same.
- **Seed lots** (`PURCHASE_RECEIPTS` View, and Export for the export; receipts are not zone-scoped): a seed lot is a Seed line of a Completed receipt. `GET /api/seed-lots` takes `from`, `to` (date received), `supplierId`, `itemId`, `zoneId` (lots received for the zone or used in it), `state` (one or several, comma separated), `search` (receipt code, seed lot code `PN000123-1`, supplier lot code, item code or name), `page`, `pageSize` (at most 100), `sort` (`code` default, `receivedAt`, `remaining`), `sortDir` (`desc` default) and answers `{ items, total, page, pageSize, totals, stateCounts }`. Each item: `id` (the receipt line), `seedLotCode`, `receiptId`, `receiptCode`, `receivedAt`, `supplier`, `item`, `product?`, `unitName`, `receivedQty`, `returnedQty`, `nursingQty`, `nurseryLossQty`, `seededQty`, `issuedOtherQty`, `remainingQty`, `availableQty`, `state`, `lastUsedAt?`. Quantities are in the base unit of the item: nursing = pieces in Nursing batches plus the pool left in Completed ones; nursery loss = pieces that did not survive plus what a close wrote off; seeded = hung directly or from the nursery; issued = Completed manual issues and disposals; remaining = received - returned - seeded - nursery loss - issued (in the nursery or in the stock); available = remaining - nursing. **State** (derived, never stored): while something remains, `Nursing` when the nursery holds pieces, `PartlySeeded` when some was seeded, lost or issued, else `Unused`; once nothing remains, `FullySeeded` (seeded, nothing returned or issued; a loss in the nursery is allowed), `FullyReturned`, `FullyLost` (only lost or issued) or `UsedUp` (any other mix). `GET /api/seed-lots/{lineId}` is the row plus `nurseryBatches`, `seedings`, `returns`, `issues`, `cycles` and `harvestLots`; `GET /api/seed-lots/export` is the list as .xlsx. **Harvest and sales figures (SEED-01, 18/11/2026)**: each item of the list page and the detail also carries `harvestedKg`, `soldKg`, `returnedFromSaleKg` and `figuresEstimated` (new fields only; `state` and `stateCounts` stay, the web screen no longer shows them; the export does not carry the new fields). They are the kg of the cycles the lot was hung in: a harvest lot counts its weighed kg, else its estimated kg; `soldKg` = invoiced - returned (cancelled invoices ignored, corrected ones at the latest kg, as `LotSalesFigures`); `returnedFromSaleKg` = kg on sales returns that are not cancelled. A cycle fed from a nursery batch has one lot and gives it all its kg; a cycle seeded from several lots shares its kg in proportion to the pieces each lot hung in it. `figuresEstimated` is true when a cycle is shared or a harvest lot is not weighed yet. Worked in three set queries per page (seeding of the cycles, harvest lots of the cycles, sales of those lots), no query per lot.

### Audit log

Every create, edit and status change writes one `AuditEvent` in the same save as the change (GBR-02, NFR-02): who acted
(an account, an employee of the employee app, or the system when neither is set), when, the action
(`FarmingZone.Updated`, `Account.StatusChanged`…), the record (type, id, code), the zone, the reason and the values
before and after. Fields that may hold a password, a code or a token are removed when the row is written and again
when it is shown. Events are never edited or deleted and no job cleans them up (kept at least 5 years).

Only the Cooperative Owner searches it (ACCOUNT-10, BR-ACCOUNT-13); every other caller gets 403 MSG-GEN-08:
- `GET /api/audit-events`: newest first, paged (`page`, `pageSize` up to 100). `from`/`to` are Vietnam dates, both
  included: the last 30 days by default, at most 5 years, else MSG-GEN-01. Filters: one person (`actorAccountId`,
  `actorEmployeeId` or `system=true`), `action` and `recordType` (exact values), `recordCode` (codes starting with it),
  `zoneId`. No match: 200 with an empty page and MSG-GEN-10 in `message`.
- `GET /api/audit-events/facets`: the actions, record types and people to offer in the filters.
- `GET /api/audit-events/{id}`: the event with `before` and `after` (JSON).
- `GET /api/audit-events/{id}/record`: the record of the event as it is now, or 404 MSG-ACCOUNT-22 when it no longer
  exists (its events stay in the log).

### VietQR sale and bank notifications (SALE-05 steps 7-8, GFR-08, API-13)

- `POST /api/sales-invoices` with `payment.rest = "VietQr"` (all of the total, or the rest after `cashAmount`; a walk-in customer
  too) answers **202** with the sale awaiting its transfer: `{ code, status: "Awaiting", zone, customer, walkInCustomer, total,
  cashAmount, toPayAmount, receivedAmount, vietQr: { payload, bankBin, bankName, accountNumber, accountHolderName, amount,
  content }, createdAt, createdBy, invoiceId, invoice, endedAt }`. The code (HD…) is given now and kept (BR-SALE-08); nothing is
  saved yet: no invoice, payment, debt or lot close. `payload` is the EMVCo text the client draws as the QR image, made again on
  every read for the receiving bank account set at that time. Without a receiving bank account: 409 MSG-SALE-13. The questions
  (MSG-SALE-10/11/12) and `deduplicationKey` work as for any sale (the same key answers the same 202, or the 201 once saved).
- `GET /api/sales-invoices/pending/{code}` is the poll of the port (every 2–3 s): `Awaiting`, then `Completed` with `invoice`
  (the saved invoice and MSG-SALE-06, e.g. "Invoice HD000123 is completed: 8,000,000 VND received by VietQR; 4,000,000 VND
  collected in cash."); the end of the sale then goes on with `POST /api/sales-invoices/{id}/finish`. `GET
  /api/sales-invoices/pending` lists the sales still Awaiting in the caller's zones (an app that was closed finds its sale again).
- `POST /api/sales-invoices/pending/{code}/change-payment` `{ payment }` (Cash, part in cash with Pay later, or Pay later; not
  VietQR) saves the invoice under the same code as a new sale at the port (201, same questions); status `PaymentChanged`.
- `POST /api/sales-invoices/pending/{code}/stop` `{ confirmed }`: without `confirmed: true`, 409 MSG-SALE-14 (`confirmationField`
  = `confirmed`); with it, status `Stopped`, nothing saved but the audit row `SalesInvoice.SaleStopped` (record code = the code,
  reason "Stop sale").
- These need SALES_INVOICES Create in the zone of the sale (the Zone Managers of the zone and the Owner; others 403). A sale that
  already ended answers 409 MSG-GEN-09 with `status` and `invoiceId`. A pending sale lasts until the end of its business day: the daily job stops it
  automatically (PM answer of 12/11/2026, see Background jobs). Its harvest lots cannot be
  removed meanwhile (HARVEST-01.1: 409 MSG-GEN-09 "… is on a sale awaiting its transfer"), and the daily close (GFR-05) leaves its kg out of
  the leftover kg it records, because the transfer invoices them on the closed lot; a sale of an earlier day is already stopped when the close runs.
- `POST /api/integrations/payment-notifications` (API-13) is anonymous and rate limited (`RateLimits:PaymentNotificationsPerMinute`,
  256 KB): a notification the adapter does not find authentic answers 401. The fake adapter's format is
  `{ "transactions": [ { "bankReference", "accountNumber", "amount", "content", "occurredAt", "direction": "In" | "Out" } ] }`,
  signed as `X-Omts-Signature: sha256=<hex of HMAC-SHA256(raw body, Payments:Fake:WebhookSecret)>`. 200 lists for each transfer
  `{ bankReference, outcome, paymentCode, invoiceCode, reason }`; 400 for a body it cannot read; 5xx when the save failed, so the
  service retries. Each transfer is recorded once per bank reference (unique index on the reference of VietQR payments, so also
  when the service reports it twice at once), and not at all when the Accountant already recorded a payment with that reference
  that is not cancelled (the reference trimmed, as MSG-DEBT-05 compares it): `AlreadyRecorded` names the payment already recorded.
  A transfer time before 2000 or more than a day ahead is `Invalid`. A sale that can no longer be saved when its transfer arrives
  (a record it names is gone) never fails the notification: it is stopped by the system (audit `SalesInvoice.SaleStopped`, the
  reason says why) and the transfer is kept `WithoutCustomer`.
- What a transfer becomes (GFR-08), a customer payment with the method VietQR code of the invoice, Awaiting confirmation,
  recorded by the system (audit actor: the system), with the amount, bank reference and time reported:
  - `Ignored` (nothing recorded): `Outgoing`, `NotTheReceivingAccount` (another account, or the account before a change,
    BR-DEBT-01), `NoInvoiceCode` (no `HD` + 6 to 12 digits in the content, not glued to letters before it; case and a separator after HD are tolerated),
    `Invalid`.
  - `PaidInvoice`: a saved invoice has the code (a later payment of a Pay later invoice, a sale whose payment method was changed,
    a cancelled invoice): a payment of its customer (none for a walk-in invoice) that covers it first.
  - `CompletedSale`: the sale awaiting it, when the amount is at least `toPayAmount`, is saved Completed, Pay now, at the prices
    shown, with the transfer (its whole amount) and the cash part as its two payments, the lots closed (one closed meanwhile stays
    as it is) and MSG-SALE-08 to the Owners; the invoice and its cash are the Zone Manager's (creator, collector, audit actor).
  - `WithoutCustomer`: no saved invoice has the code (Stop sale, an unknown code, or less than the sale awaits, which then keeps
    waiting and shows it in `receivedAmount`): no customer and no invoice, the code kept in `transferInvoiceCode` of the
    payment, listed Awaiting confirmation for the Accountant (DEBT-02.6). Until DEBT-02.6 (settle: attach to a profile, or pay it back) is available, such a payment cannot be confirmed (409 MSG-GEN-09 "Customer payment PT… is without a customer profile …",
    `needsSettlement: true`), so it stays in the list; it can be cancelled.

### Trader payments from the Zalo Mini App (Checkout SDK + payOS, decision of 21/11/2026)

A trader pays one invoice still owed from the Mini App. Zalo requires its Checkout SDK for a payment inside a Mini App, and payOS is the third party (as
`payment-sandbox` tried it). Only the Mini App uses this; the web keeps the VietQR sale and the bank notifications above. Every route is the trader's own
(`[RequireTrader]`, another trader's invoice or checkout answers 404); `Checkout:Provider` unset answers 503 MSG04.

- `POST /api/me/invoices/{invoiceId}/checkout`: what is left to pay on the invoice (`SalesInvoicePaidState.Unpaid`) becomes a customer payment
  Pending transfer into the cash book Bank account of the payOS channel (`Checkout:PayOsBankBin` and `Checkout:PayOsAccountNumber`; unset, the receiving bank
  account) (as `POST /api/customer-payments/pending-transfer`, the note "Thương lái trả qua Zalo Mini App (payOS)",
  the trader as recorder), a payOS link of the same amount whose transfer content is the payment code (only the order code, the amount and that code are sent
  to payOS), and the signed `CheckoutSDK.createOrder` parameters (`checkout`: `amount`, `desc`, `item`, `extradata`, `method` of the custom method,
  `mac`). 201 with the checkout; 200 with the one still being paid (same amount, more than 2 minutes left of its 15); an older one that expired or asks
  another amount is cancelled first (its payment never counted). 409 MSG-GEN-09 for an invoice paid or cancelled; 400 `Cash account` when the cash book has
  no Bank account for it; 503 MSG04 when payOS refuses or does not answer (nothing is saved).
- `POST /api/me/checkouts/{id}/zalo-order` `{ zaloOrderId }`: the order of the `PAY_BY_CUSTOM_METHOD` event; another one replaces it until Zalo accepted a
  report (then 409 MSG-GEN-09).
- `GET /api/me/checkouts/{id}`: polled by the QR screen. `paymentStatusCode` (PENDING_TRANSFER, COMPLETED, CANCELLED), `receivedAmount`, `paidInFull`
  (Completed with at least the amount asked), `expired`, `qr` (the payOS VietQR text, bank, account, holder, amount, transfer content), `zaloReported`.
  The client never decides that the money arrived.
- `POST /api/integrations/payos` (the webhook of the payOS channel; anonymous, rate limited as API-13): 401 unless the signature of
  `Checkout:PayOsChecksumKey` over `data` is right. A notification that is not a payment made (`code` other than "00") or names no checkout (the test payOS
  sends when the webhook is registered) records nothing (200, `ignored`). Otherwise the transfer goes through API-13 with the payment code and the invoice
  code as its content: the payment waiting for it is Completed with what arrived (a short transfer is a part payment), a bank reference already recorded
  records nothing (`AlreadyRecorded`), and money for a payment already cancelled pays the invoice (`PaidInvoice`), so no money that arrived is lost.
- Zalo is told (`custom-callback-payment`, `resultCode` 1, through `Zalo:ProxyUrl`) once the payment is Completed in full and the trader's Zalo order is
  known: right after the notification or the link, then again by the poll and by a job every `Checkout:ReportRetrySeconds` (60) until Zalo accepts,
  at most 30 times. A part payment is never reported as paid.
- The payOS link must pay into that cash book account (a link that pays into another account only logs a warning). Money for a payment already cancelled
  is recorded on the invoice only through the receiving bank account, as API-13 records it; without one it is logged and left to the Accountant.
- Development: `Checkout:Provider` = `Fake` makes up the payOS links and keeps the Zalo reports in memory; the transfer is simulated with
  `POST /api/dev/payment-notifications` and the payment code as `content`, or with a payOS notification signed with `Checkout:PayOsChecksumKey`.

### Settling a payment without a customer (DEBT-02.6)

A transfer held `WithoutCustomer` (no customer, no saved invoice) is marked `needsSettlement: true` in the rows of
`GET /api/customer-payments/awaiting-confirmation` (and `GET /api/customer-payments/{id}`) while it is Awaiting confirmation. The
Accountant or the Owner (`CUSTOMER_PAYMENTS` Edit; Zone Manager and trader 403, anonymous 401) settles it in the same list save as
the confirmations and cancellations, `POST /api/customer-payments/confirmations`, which now also takes `settle`: one entry per
payment, each with exactly one choice (400 MSG-GEN-01 `Settlement` otherwise, with `paymentId`):

- `{ id, customerId, allocations? }` attaches the payment to a customer profile. It is allocated like every other payment:
  `allocations` `[{ invoiceId, amount }]` names the unpaid invoices of the profile it pays (at most what each has left, together at
  most the amount), left out it covers the oldest unpaid invoices first, `[]` only takes it off the debt. It becomes **Completed**
  (confirmed by the caller), counts in the debt of the profile and, with the bank account it arrived in (`cashAccount`), is a line of
  the Bank fund of the cash book. A merged profile is 409 MSG-GEN-09, an unknown one 400 `Customer`; two payments of one list on one
  profile cannot pay the same invoice twice (400 `Allocation n`, naming the payment).
- `{ id, payBack: { paymentDate, method, bankReference?, note?, confirmDuplicateBankReference?, cashAccountId? } }` pays all of it
  back: the payment becomes **Completed** with no customer and a money paid back of the same amount is recorded (Completed, no
  customer, `paidBackForPaymentId` = the payment, collected by the caller). `method` is `Cash` (the Cash fund) or `OtherTransfer`
  (needs `bankReference`, MSG-DEBT-05 when a payment that is not cancelled has it, also an earlier pay back of the same list; leaves
  the Bank account `cashAccountId`, by default the one the transfer arrived in). `paymentDate` is not after today and **not before the
  date of the transfer**; invalid fields are named (`Payment date`, `Method`, `Bank reference`, `Cash account`, `Note`). Both lines are in
  the cash book and no customer debt moves.

200 answers the list as before plus `settled: [{ payment, paidBack, debtAfter }]` (`debtAfter` null when paid back; it is the profile's debt after the whole list, so two payments of one profile show the same number). Only payments
still Awaiting confirmation can be named: a settlement sent again, or lost to a parallel save (row version, unique index on the money
paid back), is 409 MSG-GEN-09 naming the payment and applies nothing twice. A payment that is not marked (already on a profile, cash at
the port) is 409 MSG-GEN-09 too. One MSG-DEBT-03 goes to the Owners for the whole list (BR-DEBT-06): `settled_codes`, `settled_count`,
`settled_more_count`, `settled_amount` join the confirmed and cancelled ones, `amount` covers all three, and `debts` also lists the
profiles a payment was attached to. Audit: `CustomerPayment.Settled` (settlement kind, profile, bank account, parts, money paid back),
`CustomerPayment.Recorded` on the money paid back. Money paid back is cancelled with `POST /api/customer-payments/{id}/cancel`: the
payment it settled goes back to Awaiting confirmation, out of the cash book, marked again (audit `CustomerPayment.SettlementUndone`);
while money paid back stands, cancelling the payment itself is 409 MSG-GEN-09 `hasMoneyPaidBack: true`. A transfer short of a sale
still awaiting it counts in `receivedAmount` of that sale only while it is Awaiting confirmation.

### Background jobs

Every daily job runs on one base (`DailyJobHostedService`): a run time in business time (UTC+7), an optional catch-up run
when the host starts after it, and a failed run logged without stopping the next day's.


`WaterMeasurementOverdueJob` runs every day at 06:00 (Asia/Ho_Chi_Minh), GFR-04: each In Use zone with a raft that has an
Open cycle counts from the later of its latest Active water measurement and its earliest Open cycle start. More than one
calendar month after that date it gets MSG-WATER-06, sent to the Zone Managers of the zone, or to the Owners when it has
none, and sent again a month after the last reminder while no new measurement moves the date. A void moves the date back;
the repeat still counts from the last reminder. Like the expiry reminder (07:00), a host started after the run time
runs it once at start: a host that was down across 06:00 sends the reminders when it next starts after 06:00. A second run
on the same day sends nothing. Each zone is saved on its own: a zone saved during the run, or one with nobody to notify,
is simply checked again the next day, and the other zones are not affected.

`PendingSaleEndOfDayStopJob` (the end of the business day, council of 03/11, PM answer of 12/11/2026) runs first in the same run as
the daily close check below, so before it, also in the catch-up run: every sale still awaiting its VietQR transfer that was created
before the business day of the run began (UTC+7) is stopped by the system, the Stop of the port (BR-SALE-08): status `Stopped`,
nothing saved on the invoice side, the audit row `SalesInvoice.SaleStopped` with no actor and the reason "Stop sale: the transfer
did not arrive by the end of the business day". A transfer that comes later is kept as a payment without a customer
(`WithoutCustomer`). A sale a transfer, a change of payment or a stop by hand ended meanwhile is left as it ended: each sale is
saved on its own and a transfer arriving at that moment (the same row version) makes the stop read the sale again and find it
ended; a second run, or a second app instance, stops nothing more. Its kg are then leftover when their lot closes in the same run.

`HarvestLotAutoCloseJob` runs every day at 00:05 (Asia/Ho_Chi_Minh), GFR-05: a harvest lot still for sale (Open or Reopened)
counts its days before automatic close (harvest settings) from its harvest date, or from the day it was last reopened. When
that day plus the days is before today, the lot is Closed with today as the close date, the reason `TimeLimitReached` and its
remaining kg as the leftover kg, and the audit row has the system as the actor: with 3 days, a lot harvested on 26/10 is
closed by the check of 30/10. The check of the day before (29/10) sends MSG-HARVEST-05 once, and the lot keeps the close date
it announced (`AutoCloseWarnedFor`), so a second run, the catch-up at start or a second app instance sends nothing more; a
reopen or new days give a new close date, warned once in its turn. A host down across the checks closes the overdue lots at
its next run (catch-up at start, like the other reminders), with that day as the close date. Each lot is saved on its own: a
lot saved meanwhile (a sale, a close by hand, the other instance) is read and checked once more at once.

The expiry reminder (GFR-03, `ZoneLicenseReminderJob`, 07:00) checks the zone licenses and the cooperative certifications.
It notifies the active Cooperative Owners only (mandatory push), once when a record becomes Expiring soon (30 days or fewer,
MSG-FARM-01) and once when it becomes Expired (MSG-FARM-02); a record found already Expired gets the Expired notice only.
Every license of an In Use zone that is not Replaced is reminded on its own state, not only the newest one: a Replaced
license and the licenses of a Discontinued zone are not. After a resume, each of these licenses gets the notice of its
current state at the next check if that notice was not sent yet; nothing is sent twice. A new expiry date re-arms both
notices.

A replaced raft photo, license file, cooperative cover photo, certificate file, cultivation log photo or harvest lot photo is removed right after the save (a removed
certification, cover photo or cultivation log too), and the upload of a failed save right away. `OrphanFileCleanupJob` runs every day at
03:00 (Asia/Ho_Chi_Minh), with no catch-up at start unless `CatchUpOnStart` is set, and deletes what such a removal left behind: raft photos (`raft-photos/`), license files
(`zone-licenses/`), cover photos (`cooperative-covers/`), certificate files (`cooperative-certifications/`), cultivation log photos (`cultivation-log-photos/`), harvest lot photos
(`harvest-lot-photos/`), quality claim photos (`claim-photos/`, a claim whose save failed after its uploads) and water meter photos (`water-meter-photos/`, a recognition that was never saved as a measurement) that no record points to and that were
written more than the grace period (24 hours) ago. No other storage folder is scanned, so cooperative logos are kept for ever.

**Every environment must use its own storage container (`Storage:AzureBlob:Container`) or folder
(`Storage:Local:RootPath`); never share one between environments.** The job deletes the files its own database does not
point to, so on a shared storage it would delete the files of the other environment. As a safeguard only, a raft photo or
license folder in which not one stored file (old or new) is referenced by the database is skipped: nothing is deleted
there and a warning is logged. Cover photos and certificate files may all be unreferenced in a real database (cover
removed, last certification removed), so their folders are skipped only when the database holds no cooperative. Every
cultivation log may be removed too, so its folder is skipped when the database holds no cooperative or has no
`CultivationLog` audit row: it never held a log, while those rows stay after the last log is removed. Harvest lot photos follow the same rule, with the `HarvestLot` audit row.

## Address pickers and e-invoice info (customers and suppliers)

- `GET api/administrative-units/provinces` and `GET api/administrative-units/provinces/{code}/wards`: the two-level list in force since 1/7/2025 (`code`, `name`, `divisionType`, wards also `provinceCode`), read from an outside list and kept 24 hours; any signed-in staff account. 404 for a code that is not a province; 503 (code MSG04, Vietnamese text) while the list cannot be read.
- `GET api/tax-lookup/{taxCode}`: a company by tax code (10 digits, or 10 digits, a hyphen and 3 digits; otherwise 400 MSG-GEN-01 on `Tax code`) with its address split into `province`, `ward` and `street`; kept 24 hours per tax code; 404 when no company has it, 503 when the registry cannot be read; 30 a minute per account (`RateLimits:TaxLookupsPerMinute`, 429); only for roles that may create or edit customers or suppliers. Only the tax code leaves OMTS.
- Customer and supplier save, view, list, import and export carry `provinceCode`, `wardCode` (names stay in `province` and `ward`, the street in `address`; `provinceName`, `wardName` and `street` repeat them), `invoiceBuyerType` (Organization, Individual, Consumer; suppliers never Consumer), `invoiceName` (stored in `CompanyName`), `taxCode` (Organization), `personalTaxId` (Individual, detail only, never in a list, export or audit log; replaces `identityNumber`), `invoiceAddressSameAsContact`, `invoiceProvinceCode/Name`, `invoiceWardCode/Name`, `invoiceStreet` and `invoiceEmail`; the view adds `addressNeedsReview` (a name kept as text with no code).
- Rules (sent `invoiceBuyerType`): Organization needs name, tax code, email and address; Individual needs name and address; Consumer keeps nothing. An address is province, ward and street (an old text entry counts). A ward must belong to its province, checked against the cached list and skipped while the list is down. A customer that sends no buyer type is a Consumer; a supplier that sends none keeps its invoice info (a new one is an Individual, or an Organization when a tax code is sent) and is not checked.
