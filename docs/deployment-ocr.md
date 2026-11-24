# OCR of water meter photos (WATER-01)

The api reads the meter photo with a PaddleOCR sidecar (`tools/ocr-service`): PP-OCRv5 mobile detection and the latin
recognition model, CPU only. The models are baked into the image, so nothing leaves the host: the photo goes from the api
to the sidecar inside the compose network. What it reads is only a suggestion: the user checks every value against the
photo and confirms it (BR-WATER-02). A value that was guessed is flagged uncertain.

## Flow

1. The Mini App sends the whole photo and, when the user framed the display, `crop` = `left,top,right,bottom` (fractions of
   the photo, each side at least 0.05) to `POST /api/zones/{zoneId}/water-measurements/recognize`.
2. The api stores the **whole** photo as the evidence (`photoKey`). Then it posts the photo and the crop to the sidecar.
3. The sidecar crops the photo, downsizes it to `OCR_MAX_SIDE` (640) and answers the text lines with their score and box.
   - JPEG is decoded at 1/2, 1/4 or 1/8 scale when that still leaves enough pixels, so a 48 MP phone photo fits the cap.
   - It refuses with 413: JPEG above `OCR_MAX_PIXELS` (50 MP), PNG above half of it, and formats other than JPEG and PNG.
4. `MeterReadingParser` (src/OMTS.Infrastructure/Ocr/Paddle) assigns the values:
   - **By label:**
     - A unit right after the number belongs to it: °C or a C, ‰, ppt, PSU, pH.
     - A name right before the number belongs to it: pH, SAL, Temp.
     - So in "pH 7.82 Sal 25.1" the "Sal" goes with 25.1.
   - **Unlabelled values:** assigned only through `Ocr:Paddle:Layout`, the meter's display order from top to bottom.
     - Only when the number of unlabelled values equals the number of layout slots still open.
     - Only within the usual range for farm water (pH 5–10, salinity 5–40 ‰, 10–40 °C).
   - **Ignored:**
     - Conductivity and TDS (µS, mS, ppm, EC).
     - Model codes (`pH7110`).
     - Printed ranges (`-20°C to +70°C`, `5–40 °C`).
     - Clocks and dates (`10:25`, `24/11`).
     - Digits that are not ASCII.
   - **Guesses** get confidence 0.5, so the app asks the user to check them:
     - A lost decimal point put back with the meter's decimals (`Ocr:Paddle:*Decimals`).
     - Decimals other than the meter's.
     - Rounding to 2 decimals.
     - A layout assignment.
     - A labelled value outside the usual range.
5. A value that is not read, and any sidecar error or timeout, ends as "not read" (MSG-WATER-01): the user types it.

Measured on 10 photos of real meters (Wikimedia Commons), under a 700 MB cap with no swap. Details are in the OCR spike
report (OMTS-228).

| Max side | Photo | Digits right | Peak memory | Time per photo |
|---|---|---|---|---|
| 1280 px | whole | killed (out of memory) | > 700 MB | |
| 960 px | whole | 11/14 | 644 MB | 2.2 s, up to 20 s |
| 640 px | whole | 7/14 | 373 MB | 1.1 s |
| **640 px** | **display crop** | **9/14** | **373 MB** | **0.4 s** |

Tesseract read 0–1/14, so it is not used.

## Settings

| Setting | Where | Value |
|---|---|---|
| `OCR_PROVIDER` | `/opt/omts/.env` | `Paddle` (unset = not read, manual entry) |
| `OCR_IMAGE` | `/opt/omts/.env` | `ghcr.io/<owner>/omts-backend-ocr:sha-xxxxxxx`, from the "OCR image" workflow |
| `COMPOSE_PROFILES` | `/opt/omts/.env` | add `ocr` (e.g. `origin-front,localdb,ocr`) |
| `OCR_METER_LAYOUT` | `/opt/omts/.env` | optional, e.g. `Ph,Temperature`, once the meter model is known |
| `Ocr:Paddle:BaseUrl` | compose.yml | `http://ocr:8000` |
| `Ocr:Paddle:PhDecimals` / `SalinityDecimals` / `TemperatureDecimals` | appsettings or env | 2 / 1 / 1, set from the meter model |

The host refuses to start when the provider is `Paddle` and the URL is missing or not http(s), or when the layout names an
unknown indicator.

With `ocr` in `COMPOSE_PROFILES` but `OCR_IMAGE` empty, `docker compose config` fails ("neither an image nor a build
context"). `deploy.sh` then rolls back every API deploy until `.env` is fixed. Set both together.

## First deploy on the staging VPS

The `ocr` service has a 600 MB hard cap and the highest OOM score: under memory pressure the kernel stops it first.

1. Run the **OCR image** workflow on develop. It also runs by itself when `tools/ocr-service` changes there.
2. The first push creates the GHCR package `omts-backend-ocr` as **private**. Make it public, like the API package
   (GitHub > Packages > omts-backend-ocr > Package settings), or the VPS cannot pull it.
3. On the VPS, back up `.env`, then set `OCR_IMAGE`, `OCR_PROVIDER=Paddle` and add `ocr` to `COMPOSE_PROFILES`.
4. Pull the image once by hand (about 2.3 GB): `docker compose --project-directory /opt/omts pull ocr`.
5. `docker compose --project-directory /opt/omts up -d ocr api`.
6. Check:
   - `docker compose exec ocr python -c "import urllib.request;print(urllib.request.urlopen('http://127.0.0.1:8000/health').read())"` answers `"status": "ok"`.
   - The api log has no OCR warning at startup.
   - A meter photo taken in the Mini App gets suggestions.

Turn it off: set `OCR_PROVIDER=` (empty) and run `up -d api`. Users then type the values; nothing else changes.
