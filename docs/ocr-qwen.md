# Reading photos with Qwen (Alibaba Cloud), as a fallback behind Gemini

Qwen (model `qwen3.8-omni-flash` on Alibaba Cloud Model Studio, OpenAI-compatible API) reads the same photos as Gemini
([ocr-gemini.md](ocr-gemini.md)). Two uses:

- **Fallback (the reason it exists):** `Ocr:Provider=Gemini` and `Ocr:Fallback=Qwen`. When the Gemini read of a photo fails, the same photo
  is read once with Qwen. Staging uses a free Gemini key (20 requests a day per model), so the licence map reader (`gemini-3.5-flash`)
  ends `Failed`/`UNAVAILABLE` after a few scans; with the fallback those scans are read by Qwen.
- **Primary:** `Ocr:Provider=Qwen` reads every photo with Qwen only (no Gemini key needed).

## Privacy and cost: read this before switching it on

- **The photo goes to Alibaba Cloud, Singapore** (`dashscope-intl.aliyuncs.com`, https only). With the fallback on, a photo whose Gemini
  read failed goes to **both** Google and Alibaba Cloud; with it off, nothing goes to Alibaba. Tell the cooperative before real photos
  are scanned on a deployment with the fallback on.
- New Model Studio accounts get a free quota of 1 million tokens per model for 90 days. One licence map is about 5 000 tokens, so the
  quota is far from the limit; after it, calls are billed (or refused with 403 when the account is set to "free tier only", which the
  server logs as an error and treats like a refused key: `NOT_CONFIGURED`).
- The server sends the photo as the user took it, base64 inside a `data:` URL; it never resizes it. Logs hold the HTTP status, finish
  reason, token counts (prompt, output, reasoning) and milliseconds. Never the key, the image, the prompt or the model text, and no error
  body of the provider is passed on or logged.
- The key goes in the `Authorization: Bearer` header only. Environment or user-secrets only, never appsettings.

## Settings

| Setting (environment variable in the staging `.env`) | Meaning |
|---|---|
| `Ocr:Provider` (`OCR_PROVIDER`) | `Gemini` (primary Gemini), `Qwen` (primary Qwen), empty (off). |
| `Ocr:Fallback` (`OCR_FALLBACK`) | Empty (no fallback) or `Qwen`. Used only behind `Provider=Gemini`; behind an empty provider it does nothing (warned at startup). Any other value, or `Qwen` with `Provider=Qwen`, stops the host. |
| `Ocr:Qwen:ApiKey` (`OCR_QWEN_API_KEY`) | Required with the Qwen fallback or provider: **the host does not start without it** (same rule as the Gemini key). |
| `Ocr:Qwen:BaseUrl` | Default `https://dashscope-intl.aliyuncs.com/compatible-mode/v1/`; the call goes to `{BaseUrl}chat/completions`. Must be https. |
| `Ocr:Qwen:Readers:Meter\|ZoneMap\|RaftForm:Model` | Default `qwen3.8-omni-flash` for all three. Letters, digits, `.`, `-`, `_`. The staging compose also passes `OCR_QWEN_ZONEMAP_MODEL` for the licence map reader. |
| `Ocr:Qwen:Readers:*:TimeoutSeconds` | The whole call, the streamed answer included. Defaults 25 (meter), 90 (licence map; the sample sheet took about 45 s), 45 (raft form). After it the result is `TIMEOUT`. |
| `Ocr:Qwen:Readers:*:Stream` | Default `true`: the omni models require streaming (server-sent events). Set `false` only for a model that answers a plain request. |
| `Ocr:Qwen:DisableThinking` | Default `true`: sends `"enable_thinking": false` (a DashScope extension) so the model skips its reasoning pass (about 2 000 of 5 000 tokens on the sample map). Set `false` (`OCR_QWEN_DISABLE_THINKING=false` on staging) to leave the field out if the endpoint or a model rejects it (HTTP 400, every Qwen read would be `BAD_RESPONSE`). |
| `Ocr:Qwen:JsonMode` | Default `false`. `true` adds `response_format: {"type":"json_object"}`; leave it off unless the model accepts it. The prompt already asks for one JSON object. |
| `Ocr:ReadBudgetSeconds` | Default 115: the time Gemini and Qwen share for one licence map or raft form scan (see Time budget). |

`appsettings.json` holds no key. The startup log prints the models and timeouts:
`OCR provider fallback Qwen: meter … licence map … raft form …, thinking off, scan budget 115 s` (after the Gemini line).

## What Qwen is asked, and what is done with the answer

- **The same prompts as Gemini** (`Ocr/Gemini/Prompts`). Gemini takes its JSON schema as a request field; the omni models do not
  enforce one, so the schema is written under the prompt as a template (`"ph": null or { "value": string or null, ... }`) with the order
  of the keys, plus "one JSON object, no Markdown fence".
- **The same readers and normalisers**: the answer goes through the same shapes (`GeminiMeterOcrService`, `GeminiZoneMapReader`,
  `GeminiRaftFormReader`), then the same checks as before (the number parsing, the confidence set by the server, the grid areas and
  coordinate checks of the licence map, the Owner's review). Qwen misread 3 of 54 coordinates in the manual test of 26/11/2026; those are
  exactly the errors the existing area check and the Owner's review exist for, so they are not treated differently.
- A Markdown fence (` ```json `) around the answer is removed (the omni models sometimes add one); anything else that is not one JSON
  object is `BAD_RESPONSE`. The reasoning text (`reasoning_content`) is never read as the answer.

## Fallback rules

The decision is in `FallbackVisionClient`, one place for the meter and both scans.

| Gemini ended | Qwen is tried? |
|---|---|
| ok | no |
| `UNAVAILABLE` (429, 5xx, network) | yes |
| `NOT_CONFIGURED` (401, 403, 404: key or model refused) | yes |
| `TIMEOUT` | yes, if the budget has 10 s or more left |
| `BAD_RESPONSE` (answer not the JSON asked for, early stop, safety block, HTTP 400) | yes, once |
| the caller gave up (request cancelled) | no |

- Qwen is tried **once**, never repeated. Qwen has no retry of its own: when it is the fallback it is the retry.
- **When both fail the result is Gemini's code**, so "try later" stays "try later" even when the Qwen key is wrong. The Qwen code is in
  the log (`Qwen ended … too`).
- **The API contract does not change** and does not say which provider read the photo. The log does: `OCR ZoneMap: read by Gemini`,
  or `OCR ZoneMap: read by fallback Qwen after Gemini ended UNAVAILABLE, 41230 ms in all`.

Qwen failure codes: HTTP 401/403 `NOT_CONFIGURED` (logged as an error), 429, 5xx and network errors `UNAVAILABLE`, other 4xx
(400, 404, 413) `BAD_RESPONSE`, the reader timeout `TIMEOUT`, an error chunk inside the stream by its code (rate or quota `UNAVAILABLE`,
key or permission `NOT_CONFIGURED`, anything else `BAD_RESPONSE`), a stream cut off before `[DONE]` or ending with a finish reason
other than `stop` (`length`, `content_filter`) `BAD_RESPONSE`.

## Time budget

The client polls a scan for 120 s and the staging proxies cut a request at 30 s (CloudFront) or 60 s (nginx), so the two providers share
one budget per read instead of adding their timeouts:

- **Licence map and raft form (asynchronous):** `Ocr:ReadBudgetSeconds` (115 s, a little under the client's 120 s), counted from the
  start of the read (the queue wait is not in it; the worker already ends a scan that waited over 120 s). Gemini goes first with its own
  timeout (90 s for the licence map). Qwen gets the budget Gemini left, never more than its own reader timeout, and is **not started when
  less than 10 s is left**. A Gemini `429` answers in about a second, so Qwen then has the whole 90 s it needs; a Gemini `TIMEOUT` after
  90 s leaves about 25 s, too little for the licence map, but the raft form (Gemini 45 s) still has a try.
- **Meter (answered inside the request):** the budget is the primary's meter timeout (25 s, so the recognizer's 27 s still answers before
  the 30 s CloudFront limit). Gemini failing quickly (429) leaves Qwen about 23 s, enough for a short display photo with thinking off.
- The worker's own `Ocr:Scans:JobTimeoutSeconds` (180 s) stays the outer limit.

## Staging rollout

Order matters: the host does not start with `OCR_FALLBACK=Qwen` and no Qwen key.

1. Create a Model Studio key in the **Singapore** region (international console) and keep it out of git and chat.
2. In `/opt/omts/.env` on the staging host add `OCR_QWEN_API_KEY=<key>`. Keep `OCR_PROVIDER=Gemini` and the Gemini key as they are.
3. Deploy the release that carries this change (the compose file already passes `OCR_FALLBACK`, `OCR_QWEN_API_KEY`,
   `OCR_QWEN_ZONEMAP_MODEL` and `OCR_QWEN_DISABLE_THINKING`; empty values change nothing).
4. Set `OCR_FALLBACK=Qwen` in `.env` and recreate the API container (`docker compose up -d api`, or the usual deploy script).
5. Check the startup log for `OCR provider fallback Qwen: ...`, then scan one licence map. The log shows
   `OCR ZoneMap: Gemini ended UNAVAILABLE; retrying the same photo once with Qwen` when the Gemini quota is used up, then
   `Qwen ZoneMap (qwen3.8-omni-flash): HTTP 200, finishReason stop, tokens prompt … output … reasoning …`.
6. If Qwen answers HTTP 400 in the log, set `OCR_QWEN_DISABLE_THINKING=false` in `.env`, recreate the container and try again.
7. Off switch for the fallback: empty `OCR_FALLBACK` and recreate the container. Qwen only as the reader: `OCR_PROVIDER=Qwen`.

Local run: `dotnet user-secrets set "Ocr:Qwen:ApiKey" "<key>" --project src/OMTS.Api`, with `Ocr:Provider=Gemini` and `Ocr:Fallback=Qwen`
(or `Ocr:Provider=Qwen`).

## Tests

No test reaches the network: `tests/OMTS.Infrastructure.UnitTests` uses a fake `HttpMessageHandler` (stream parsing, request body,
status mapping, timeouts, log hygiene) and scripted clients (the fallback matrix and the budget). A real call is a manual check with
a key in the environment, never in CI.
