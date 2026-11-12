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

### Roles and zone access

OMTS has three fixed roles (BR-FARM-07): Cooperative Owner, Zone Manager and Accountant. They are not created, edited or
discontinued: `GET /api/roles` lists the two staff roles with their `code` (`ZONE_MANAGER`, `ACCOUNTANT`) and their
permissions, the static matrix of SRS §I.4.4 in `FixedRoles`. The Owner grants a staff account zone–role pairs
(`POST /api/accounts/{id}/grants` with `role` (code) or `roleId`, and a `zoneId` or none for All zones). A pair is Effective
until it is revoked with a reason; there are no validity dates. On records with a zone a role works only in the zones of
its pairs; records with no zone (suppliers, purchase receipts and returns, supplier payments and debts, customers,
payroll…) follow the role only, so an Accountant pair for one zone opens them (BR-FARM-08). The rafts a Zone Manager names on
a purchase receipt line are still those of the zones of their Zone Manager pairs. A staff account without an Effective pair
cannot log in.

### Farming zones and licenses

- Zones (FARM-02): `GET /api/zones/next-code` (Owner) proposes the next code `KV01`, `KV02`… (GBR-03: the highest `KVnn` in
  use, discontinued zones included, plus one); the Owner may save another code. A Discontinued zone is read-only: its
  edit answers MSG-GEN-09 until `POST /api/zones/{id}/resume` .
  The water measurement point is optional. Create and edit answer `{ zone, message, messageCode }` (MSG-GEN-05).
- Licenses (FARM-03): a license belongs to one zone (`zoneId`) and is added, edited or replaced only while that zone is In Use (MSG-GEN-09). The expiry date is required;
  the type, holder and legal scope are optional. A license is Replaced from the day the license replacing it is recorded,
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
| `EMPLOYEE_REQUEST_NEW` | MSG-LABOR-23 (LABOR-10), when an employee sends a request (leave, missed check-in or check-out) | unless turned off | Owner |
| `CULTIVATION_LOG_RECORDED` | MSG-CYCLE-04, when a cultivation log is saved, not on a correction, not to the Owner who recorded it | unless turned off | Owner |
| `CYCLE_SEEDING_COMPLETED` | MSG-CYCLE-02, when the seeding of a raft is marked complete (once per cycle), not to the Owner who marked it | unless turned off | Owner |
| `HARVEST_LOT_RECORDED` | MSG-HARVEST-01, when a harvest lot is recorded, not on a correction, not to the Owner who recorded it | unless turned off | Owner |
| `LANE_DAMAGED` | a lane of a raft is reported damaged (12/11/2026), not to the Owner who reported it; message code `MINI_APP_NOTIFICATION` (no SRS message exists); opens the raft | unless turned off | Owner |
| `LANE_OUT_OF_SERVICE` | a lane is put out of service (12/11/2026), not to the Owner who did it; message code `MINI_APP_NOTIFICATION`; opens the raft | unless turned off | Owner |
| `LANE_PROGRESS_CHANGED` | the seeded or harvested percentage of a lane is changed by hand, `PUT /api/rafts/{raftId}/lanes/{n}/progress` (12/11/2026), not to the Owner who did it; message code `MINI_APP_NOTIFICATION`; opens the raft | unless turned off | Owner |
| `NURSERY_RECORDED` | a nursery batch (ươm giống) is started, edited, cancelled, completed or its survivors or shares corrected (12/11/2026), not to the Owner who did it; message code `MINI_APP_NOTIFICATION`; opens the batch | unless turned off | Owner |
| `HARVEST_LOT_AUTO_CLOSE_WARNING` | MSG-HARVEST-05, from the daily close check (GFR-05), once for each close date of a lot still for sale that the next check closes: to the Zone Managers of the zone (All zones too), or to the Owners when it has none | unless turned off | Owner, Zone Manager |
| `CYCLE_CLOSE_REMINDER` | MSG-CYCLE-08, once per cycle, when a harvest makes every seeded lane Harvested: to the Zone Managers of the zone (All zones too), or to the Owners when it has none, not to the Owner who recorded it | always (mandatory) | Owner, Zone Manager |
| `PURCHASE_RECEIPT_COMPLETED` | MSG-PURCHASE-01, when a receipt is completed (never for a draft), also when the Owner completed it | unless turned off | Owner |
| `PURCHASE_RECEIPT_CANCELLED_ADVANCE` | MSG-PURCHASE-08, when a cancelled receipt leaves the supplier debt below zero | unless turned off | Owner, Accountant |
| `SUPPLIER_TRANSACTION` | MSG-PURCHASE-04, for a supplier payment, money paid back or a cancelled one, also when the Owner did it | always (mandatory) | Owner |
| `CUSTOMER_PAYMENT_TRANSACTION` | MSG-DEBT-03, for a customer payment, money paid back or a cancellation saved in DEBT-02, and one notice per list of confirmations (no related record when the list holds several payments), also when the Owner did it | always (mandatory) | Owner |
| `PROVENANCE_WITHDRAWN` | MSG-TRACE-05, when the Owner withdraws the origin information of a lot the trader bought (TRACE-04); the row opens nothing | unless turned off | Trader |
| `TRADER_ACCESS_REQUESTED` | a trader asked for an account from the Zalo Mini App (`POST /api/auth/zalo/trader-requests`) | unless turned off | Owner |
| `TRADER_ACCESS_APPROVED` | the Owner approved the request (`POST /api/trader-requests/{id}/approve`) | unless turned off | Trader |
| `QUALITY_CLAIM_NEW` | MSG-CLAIM-01, a new quality claim (CLAIM-01) | unless turned off | Owner |
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
  never a bank account number. Operational texts read as their fields do, digits included. At 160 the values, allowed
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

SALE-09 (`SALES_INVOICES`, zones of the caller's pairs, BR-SALE-08): the Owner, the Zone Manager of the zone and the Accountant view; the Zone Manager and the Owner also print. Any other zone answers 403 MSG-GEN-08.

- `GET /api/sales-invoices`: newest first, paged (`page`, `pageSize` up to 100). Filters: `from`/`to` (Vietnam dates, both included; today when neither is given, only `to` is that one day, only `from` runs to today, at most 5 years, else MSG-GEN-01), `zoneId`, `customerId`, `harvestLotId`, `raftId` (a line of a lot of the raft), `statusId`, `code` (part of the code, `%`, `_`, `[` are plain text), `paid` (true/false; a Cancelled invoice matches neither) and `cashCollectedByMe=true` (cash this caller collected that is Awaiting confirmation; each row then has `cashAwaitingConfirmation`). No match: 200 with an empty page and MSG-GEN-10 in `message`. A row has `code`, `invoicedAt`, `zone`, `customer` (null for a walk-in), `total`, `status`, `paymentTerm`, `isPaid` (null for a Cancelled invoice), `unpaidAmount` and `deliveryStatus`. `completedTotal` is the total of every Completed match, not of the page; `isPaid` comes from the debt allocation (BR-DEBT-05), a walk-in invoice from its own payments.
- `GET /api/sales-invoices/{id}`: the invoice with `lines`, `payments`, `sendings`, `priceCuts`, `debtEffect` (`isPaid`, `covered`, `unpaid`, `countsInCustomerDebt`, `customerDebt`) and `allowedActions` (SALE-09.1 step 3). The same fields are in the answers of `POST` create, `finish` and `resend`.
- `GET /api/sales-invoices/export` (the filters above, every page) and `GET /api/sales-invoices/{id}/print`: an `.xlsx` file for download and print, Export right in the zone of the invoice (the Accountant only views, §I.4.4 R: 403). Text is never a formula. An empty list is a header and a zero total.

`allowedActions`, on the rules of the SRS:

| Value | Offered to | When | Endpoint today |
|---|---|---|---|
| `Resend` | Zone Manager of the zone, Owner | not Cancelled, the sale is finished, the latest sending is not Sending | `POST /api/sales-invoices/{id}/resend` |
| `CreateReturn` | Zone Manager of the zone, Owner | not Cancelled, no sales return that is not cancelled | none yet: CLAIM-03 |
| `CancelReturn` | Owner | a sales return that is not cancelled (none can exist yet) | none yet: CLAIM-03.3 |
| `Correct`, `Cancel` | Owner | Completed, no sales return that is not cancelled | none yet: SALE-07, SALE-08 |
| `Print` | Whoever has Export in the zone | always | `GET /api/sales-invoices/{id}/print` |

- Weighing at the port (decision of 09/11/2026): after the harvest each cage is weighed, the trader confirms the figures, and the invoice is made from that weighing. `POST /api/port-weighings` (`purchaseRequestId`, `lots: [{ harvestLotId, cageKgs }]`; SALES_INVOICES Create in the zones of the lots) for an Accepted request that is not Fulfilled, one weighing waiting for the trader at a time; `PUT /api/port-weighings/{id}` (`lots`, `rowVersion`) corrects it until it is confirmed; `GET /api/port-weighings?purchaseRequestId=` and `GET /api/port-weighings/{id}` read it. The trader: `GET /api/me/port-weighings`, `GET /api/me/port-weighings/{id}` and `POST /api/me/port-weighings/{id}/confirm` (`rowVersion`: figures corrected since they were read answer 409). A weighing has `customerId`, `lots` with `cageKgs`, `cageCount`, `totalKg`, `confirmedAt`, `invoiceId` and `invoiceCode`.
- Public provenance page (BR-ORIGIN-01, BR-ORIGIN-05): `GET /api/origin/{qrToken}`, anonymous, the token at the end of the QR link of an invoice. It lists the Published lots of the invoice with their origin (products, zone and raft, seed lots and suppliers, seeding dates, cultivation logs, the average water of the zone while the lot grew, harvest, the kg sold and the cages weighed) and a Withdrawn lot only with its withdrawal time; never the customer, the staff or a price (decided 10/11/2026). An unknown token, a cancelled invoice or one with no published lot answers 404.
- `POST /api/sales-invoices` with a `purchaseRequestId` needs the `portWeighingId` of a confirmed weighing of that request not yet on a Completed invoice (MSG-GEN-01 `Port weighing`, MSG-GEN-09). Each line names one lot of the weighing and takes its kg (a `kg` sent must be that one), and every lot is on one line. A sale without a request gives `kg` itself. `lines[].cageKgs` and `portWeighingId` come back on the invoice, and `GET /api/sales-invoices/open-purchase-requests` gives `deliveredCages`, the cages of the weighings already on Completed invoices, next to `acceptedCages`.
- `GET /api/me/invoices` (`page`, `pageSize` up to 100, newest first; MSG-GEN-11 in `message` when empty) and `GET /api/me/invoices/{id}`: the logged-in trader's own invoices with `cageKgs`, the money, the payments and `qrUrl`, without the staff data. Staff get 403 MSG-GEN-08; an invoice of another profile is 404.


### Quality claims (CLAIM-01, CLAIM-02)

A trader reports a quality problem on one of their invoices with photos; the Cooperative Owner responds. A claim changes neither the invoice nor the customer debt (BR-CLAIM-01/02).

- `POST /api/me/sales-invoices/{id}/quality-claims` (trader, multipart: `description` up to 2000 characters, one `photos` part per photo, 1 to 10 JPEG or PNG of at most 10 MB; 201 with the claim, code `KN000123`, and MSG-GEN-05). Only an invoice of the trader's own profile (a profile merged into theirs counts; else 404) that is not cancelled, has no sales return (sales returns come with CLAIM-03: the check is the seam `IInvoiceSalesReturnCheck`) and has no other claim Awaiting response (409 MSG-GEN-09, one filtered unique index per invoice). A photo that cannot be uploaded answers 502 MSG-GEN-03 and saves nothing. The Owner gets MSG-CLAIM-01 (not a mandatory type). Answered claims do not stop a new one and OMTS sets no time limit.
- `GET /api/me/quality-claims` (own claims, newest first, MSG-GEN-11 when none) and `GET /api/me/quality-claims/{id}` (404 for another trader's): the description, photo links, status, offer and response note. The photos are private (BR-CLAIM-01): signed links for 1 hour, never on the provenance page.
- `GET /api/quality-claims` (`statusId`, `page`, `pageSize`; Awaiting response first) and `GET /api/quality-claims/{id}` (customer with phone number, invoice with lines, lots, kg, amounts, status and price cuts, the SMS sendings, `rowVersion`): QUALITY_CLAIMS View, so the Owner and the Zone Managers, who see the claims of invoices in their zones only (PM default; another zone answers 403). An Accountant has no access.
- `POST /api/quality-claims/{id}/accept` (`{ offer: "ReturnAllGoods" | "PriceCutWholeInvoice", note?, rowVersion }`) and `POST /api/quality-claims/{id}/reject` (`{ note, rowVersion }`): Owner only, while Awaiting response; a response cannot be changed (409 MSG-GEN-09, or MSG06 when the form is stale). Accepting answers 409 MSG-CLAIM-03 when the invoice was cancelled or has a sales return (it can still be rejected). The offer only records what was proposed: the return is CLAIM-03 and the price cut SALE-07 (`PUT /api/sales-invoices/{id}/correction`, PriceCutAfterClaim), which the Owner runs after the trader chooses. Neither changes here. The trader gets a push and an SMS with MSG-CLAIM-02; a message that cannot be sent undoes nothing (GFR-06), it stays in the trader's list and the sending is listed Not sent on the claim; a profile without a phone number gets a Not sent row (`NO_PHONE_NUMBER`).

### Work assignment, attendance and employee requests (LABOR-02, LABOR-04, LABOR-05, LABOR-10)

`WorkAssignment` holds one job period of an employee and day (the first one has no start time, each change of job adds one),
`Attendance` the check-in and check-out of one employee and day with their recording method, `EmployeeRequest` a leave or a missed
check-in or check-out. Hours are worked out when shown (`WorkHoursCalculator`, BR-LABOR-05) from the attendance settings in force
on the day; nothing is recorded in a pay period whose payroll is Awaiting finalization or Finalized (`MSG-LABOR-04`, BR-LABOR-13).
Zone Manager (in the zone of the raft) and Owner, except where marked Owner:

- `GET /api/workforce/assignments?day=`, `GET /api/workforce/lookups?day=`; `POST /api/workforce/assignments` (409 `MSG-LABOR-06`
  asks to move an employee, resend with `moveExisting`), `PUT`/`DELETE /api/workforce/assignments/{id}` until the employee is
  checked in (`MSG-LABOR-07`), `POST /api/workforce/crew-job` (Change job for crew, 204).
- `POST /api/workforce/assignments/{id}/check-in` and `/check-out` with `qrCode` (the code of `GET /api/me/attendance-qr`, valid
  60 seconds, else `MSG-LABOR-13`) or null to mark by hand; `POST /api/workforce/crew-start`; Staff employees:
  `GET /api/workforce/staff-attendance`, `POST /api/workforce/staff/{employeeId}/check-in|check-out`.
- Owner: `GET`/`PUT /api/workforce/attendance/{employeeId}/{day}` corrects times and job periods with a reason;
  `POST /api/workforce/leave-requests/{id}/review` approves or rejects (a rejection needs a note); `GET /api/workforce/leave-requests`
  is also read by the Zone Manager.
- The employee linked to the caller's account (`Employee.AccountId`): `GET /api/me/workforce?from=&to=` (at most 62 days),
  `GET /api/me/attendance-qr`, `POST /api/me/leave-requests` (leave, or `type` MissedCheckIn / MissedCheckOut with `day` and
  `requestedTime`), `DELETE /api/me/leave-requests/{id}` (cancel while Awaiting review).



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
  already ended answers 409 MSG-GEN-09 with `status` and `invoiceId`. A pending sale has no time limit. Its harvest lots cannot be
  removed meanwhile (HARVEST-01.1: 409 MSG-GEN-09 "… is on a sale awaiting its transfer"), and the daily close (GFR-05) leaves its kg out of
  the leftover kg it records, because the transfer invoices them on the closed lot.
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
