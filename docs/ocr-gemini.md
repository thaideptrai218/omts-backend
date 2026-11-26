# Reading photos with Google Gemini

Since 26/11/2026 every photo the server reads goes to Google Gemini (decision of 25/11/2026: no PaddleOCR fallback). This replaces
the PaddleOCR sidecar and its deployment guide.

| What | Endpoint | Style | Reader (default model, timeout) |
|---|---|---|---|
| Water meter display (WATER-01) | `POST api/zones/{zoneId}/water-measurements/recognize` | synchronous, response unchanged | `Meter`: `gemini-3.5-flash-lite`, 25 s |
| Sea-area licence map, corner tables (FARM-02) | `POST api/zones/{zoneId}/boundary-scans`, `GET …/{scanId}`; without a zone (create-zone form): `POST api/zones/boundary-scans`, `GET api/zones/boundary-scans/{scanId}` | asynchronous scan | `ZoneMap`: `gemini-3.5-flash`, 90 s |
| Raft form (FARM-04) | `POST api/rafts/form-scans`, `GET …/{scanId}` | asynchronous scan | `RaftForm`: `gemini-3.5-flash-lite`, 45 s |

A failed Gemini read can be retried once with Qwen (Alibaba Cloud): see [ocr-qwen.md](ocr-qwen.md) (`Ocr:Fallback=Qwen`). That page
also says what changes for privacy, because the photo then goes to a second company.

The API contract for the scans is in [api-behaviour-reference.md](api-behaviour-reference.md#farming-zones-and-licenses). The cage
ledger scan is not part of this release.

## Privacy and cost: read this before switching it on

- **The photo leaves the server and goes to Google** (`generativelanguage.googleapis.com`, https only). The old wording "the photo never
  leaves the host" no longer holds; the mobile app's hints change with it.
- **A free key is for tests only:** Google may use free-tier content to improve its products. **Production, and any photo of a real
  cooperative, needs a billed key.** Do not send cooperative photos with the free key.
- The server sends the photo as the user took it (JPEG or PNG, at most 10 MB, base64 inline, under Gemini's 20 MB request limit);
  there is no image library, so it never resizes or crops. The meter `crop` is accepted and ignored.
- The model only copies what is written. The prompts never state a rule the numbers obey, and the confidence is set by the server.
  Readings are suggestions: the user confirms the meter values, and the Owner reviews a map or form before anything is saved.
- Logs hold the HTTP status, the finish reason, token counts and milliseconds. Never the key, the image or the model text, and no
  Google error body is passed on to the client.

## Switching it on, and off

| Setting (environment variable in the staging `.env`) | Meaning |
|---|---|
| `Ocr:Provider` (`OCR_PROVIDER`) | `Gemini` reads photos. **Empty (the default) is the off switch:** nothing is read, meter users type the values (MSG-WATER-01) and scans end `Failed` / `NOT_CONFIGURED`. `Fake` is Development only and stops the host elsewhere. |
| `Ocr:Gemini:ApiKey` (`OCR_GEMINI_API_KEY`) | Required with Gemini: the host does not start without it. Environment or user-secrets only, never appsettings. |
| `Ocr:Gemini:BaseUrl` | Default `https://generativelanguage.googleapis.com/v1beta/`. Must be https, or the host does not start. |
| `Ocr:Gemini:Readers:Meter\|ZoneMap\|RaftForm:Model` | Model id (letters, digits, `.`, `-`, `_`). The startup log prints the model per reader. |
| `Ocr:Gemini:Readers:*:TimeoutSeconds` | The whole call, the retry included. After it the result is `TIMEOUT`. |
| `Ocr:Gemini:Readers:*:ThinkingBudget` | Optional `thinkingConfig.thinkingBudget` (0 turns thinking off); unset keeps the model default. |
| `Ocr:Scans:QueueCapacity` / `MaxConcurrent` / `ResultMinutes` / `MaxQueueWaitSeconds` | 4 waiting scans / 2 at a time / results kept 15 minutes after completion / a scan that waited over 120 s ends `TIMEOUT` without calling Gemini. |
| `RateLimits:WaterRecognitionsPerHour` / `ZoneMapScansPerHour` / `RaftFormScansPerHour` | Per account and hour: 60 / 20 / 30. Over the limit is 429. |
| `Ocr:TimeoutSeconds` | With Gemini the recognizer waits the meter reader timeout + 2 s, so the client's own timeout answers first. |

## Staging switch-over (from PaddleOCR)

The staging `.env` on the host still has `OCR_PROVIDER=Paddle`, `OCR_IMAGE` and `OCR_METER_LAYOUT`, and `ocr` in `COMPOSE_PROFILES`.
The new compose file has no `ocr` service and no `Ocr__Paddle__*` settings. An unknown provider value (such as `Paddle`) is **not an
error**: the host only warns and every photo is left unread, so do these steps in this order:

1. **Before merging:** add `OCR_GEMINI_API_KEY=<billed key>` to `/opt/omts/.env` (a free key is for tests only, see Privacy).
2. **At deploy time:** set `OCR_PROVIDER=Gemini`. Remove `ocr` from `COMPOSE_PROFILES`, and delete `OCR_IMAGE` and `OCR_METER_LAYOUT`.
3. Check the startup log line `OCR provider Gemini: meter … licence map … raft form …`, then try one meter photo.
4. Rollback or off switch: empty `OCR_PROVIDER`; users type the values.

Local run: `dotnet user-secrets set "Ocr:Gemini:ApiKey" "<key>" --project src/OMTS.Api` and `Ocr:Provider=Gemini`. Without a key,
Development keeps `Fake` (a made-up map and raft form, so the app screens can be tried).

## Failures

A failed read is always a value, never a server error:

| Cause | Code |
|---|---|
| HTTP 429 or 503 (one retry, only while at least 40 % of the timeout is left); other 5xx; network error | `UNAVAILABLE` |
| Timeout | `TIMEOUT` |
| Answer that is not the JSON asked for (a Markdown fence is not repaired), no candidate, blocked prompt, a finish reason other than `STOP` (safety, `MAX_TOKENS`…), HTTP 400 (Gemini answers it for an image it cannot process) | `BAD_RESPONSE` |
| HTTP 401, 403, 404 (key or model refused; logged as an error), no provider | `NOT_CONFIGURED` |

Other 4xx answers (for example 413) are `BAD_RESPONSE` too. For the meter a failure is `ocr.status = Failed` with the photo key, as before.

## Licence map reading

The `ZoneMap` reader copies two things, both as text, never the drawing:

- **The info table:** `coordinate_system`, `central_meridian`, and since 26/11/2026 `location` (the "Địa điểm" row, e.g. "Xã Đông Xá, xã
  Bản Sen, huyện Vân Đồn, tỉnh Quảng Ninh") and `written_total_area` (the "Diện tích đề nghị" row, e.g. "28,50 ha"). The server turns
  them into `reading.location` (trimmed, at most 300 characters, null when unread) and `reading.writtenTotalAreaHa` (a number above 0
  and at most 100 000, null when unread or unreadable; "28,50 ha" and a bare "28,50" both read). Both only prefill the create-zone form,
  which the Owner reviews: a misread total costs one edit.
- **Every table "TỌA ĐỘ CÁC ĐIỂM GÓC KHU N":** the area name, the written area and each corner row (see the API contract).

A scan can start with no zone (`POST api/zones/boundary-scans`, for the create-zone form, Owner only): it uses the same queue, limits
(20 an hour per account, shared with the zone scans) and retention, and is found only on its own route.

The `Fake` provider (Development) answers the sample map: location "Xã Đông Xá, xã Bản Sen, huyện Vân Đồn, tỉnh Quảng Ninh", total 28.5 ha,
central meridian 107°45', four areas of 12.5, 6.2, 5.8 and 4.0 ha (rectangles whose grid areas match the written ones).

## Asynchronous scans (licence map, raft form)

A scan takes 30 s to 2 min, longer than staging's nginx (`proxy_read_timeout 60s`) and CloudFront's 30 s origin timeout allow, so it is
start-then-poll: `POST` answers 202 `{ scanId }`, the client polls `GET` every 2 s and gives up after 120 s.

- The queue lives in the API process: a bounded channel of 4 waiting scans read by 2 workers (each waiting scan holds its photo, up to 10 MB
  and its base64 copies, in memory). **An account has at most one scan pending or running**: a second start is refused like a full queue.
  A full queue is 503 `OCR_BUSY` ("Máy chủ đang bận đọc ảnh, vui lòng thử lại sau ít phút."). The client polls for 120 s, so a job that
  waited longer than that when a worker takes it ends `Failed`/`TIMEOUT` without a Gemini call. A result stays readable 15 minutes
  counted from its completion; a scan still waiting or running never expires.
- **The API must run as a single instance.** A second instance does not know the scans of the first, and a restart loses the scans in
  flight; the client simply scans again. The queue is generic (`IScanQueue`), so the cage ledger scan can reuse it.
- Only the Owner who started a scan can read it; any other account, zone or route gets 404 (a scan started with no zone is found only on
  `GET api/zones/boundary-scans/{scanId}`).

## Rest contract used (checked against the google-genai SDK)

`POST {BaseUrl}models/{model}:generateContent`, header `x-goog-api-key` (never in the URL), body
`{ contents: [{ role: "user", parts: [{ inlineData: { mimeType, data } }, { text }] }], generationConfig: { temperature: 0,
responseMimeType: "application/json", responseSchema, thinkingConfig? } }`. The schema uses upper-case types (`OBJECT`, `STRING`) and
`propertyOrdering`. The answer is read from `candidates[0].content.parts[].text` (thought parts skipped), `finishReason` and
`usageMetadata.{promptTokenCount, candidatesTokenCount, thoughtsTokenCount}`.

## Tests

No test reaches the network: the client tests use a fake `HttpMessageHandler`, the scan tests a scripted reader. A real call is a
manual check with a key in the environment, never in CI.
