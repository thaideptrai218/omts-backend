"""OMTS OCR sidecar: reads the text lines of a meter photo with PaddleOCR (PP-OCRv5 mobile, latin), offline, CPU only.

Not exposed outside the compose network; only the OMTS api calls it.
- POST /recognize   body = the image bytes (JPEG/PNG, at most MAX_BYTES). Optional query crop=left,top,right,bottom as
                    fractions of the image (0..1) to read only that part. Answers
                    {"width", "height", "lines": [{"text", "score", "box": [x0, y0, x1, y1]}], "ms"}; boxes are in pixels
                    of the image that was read (after crop and downscale), lines top to bottom.
- GET  /health      {"status": "ok", "processMb", "peakMb"} once the models are loaded (see memory_mb).
One request at a time (HTTPServer is single-threaded): PaddleOCR is not thread-safe and the host has little RAM.
"""
import io
import json
import logging
import math
import os
import resource
import time
import warnings
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlparse

import numpy as np
from PIL import Image, ImageOps

MAX_BYTES = int(os.environ.get("OCR_MAX_BYTES", str(10 * 1024 * 1024)))
# Longest side of the image that is read: the server bounds memory and time itself, whatever the client sends.
# 640 keeps the peak under 400 MB; 1280 was killed under a 700 MB cap (docs/deployment-ocr.md).
MAX_SIDE = int(os.environ.get("OCR_MAX_SIDE", "640"))
# A small file can still decode to a huge image (a flat 150 MP PNG is ~450 MB of RGB): refuse it from its header.
MAX_PIXELS = int(os.environ.get("OCR_MAX_PIXELS", "50000000"))
PORT = int(os.environ.get("OCR_PORT", "8000"))

Image.MAX_IMAGE_PIXELS = MAX_PIXELS
warnings.simplefilter("error", Image.DecompressionBombWarning)

# Own handler: PaddleOCR reconfigures the root logger when it loads, which would swallow these lines.
log = logging.getLogger("omts-ocr")
_handler = logging.StreamHandler()
_handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
log.addHandler(_handler)
log.setLevel(logging.INFO)
log.propagate = False


def load_engine():
    from paddleocr import PaddleOCR

    return PaddleOCR(
        text_detection_model_name="PP-OCRv5_mobile_det",
        text_recognition_model_name="latin_PP-OCRv5_mobile_rec",
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        cpu_threads=int(os.environ.get("OCR_CPU_THREADS", "2")),
        enable_mkldnn=os.environ.get("OCR_MKLDNN", "0") == "1",
    )


def parse_crop(value: str | None) -> tuple[float, float, float, float] | None:
    """crop=left,top,right,bottom as fractions; None when absent. Raises ValueError when malformed."""
    if not value:
        return None
    left, top, right, bottom = (float(v) for v in value.split(","))
    if not (0 <= left < right <= 1 and 0 <= top < bottom <= 1):
        raise ValueError("crop must be left,top,right,bottom with 0 <= left < right <= 1 and 0 <= top < bottom <= 1")
    return left, top, right, bottom


class TooLarge(Exception):
    """The image has more pixels than MAX_PIXELS."""


def prepare(data: bytes, crop: tuple[float, float, float, float] | None) -> Image.Image:
    """Decodes at the smallest scale that still gives the crop MAX_SIDE pixels, then turns, crops and downsizes."""
    try:
        image = Image.open(io.BytesIO(data), formats=["JPEG", "PNG"])
    except (Image.DecompressionBombError, Image.DecompressionBombWarning) as exc:
        raise TooLarge() from exc
    # PNG cannot be decoded at a reduced scale, so it gets a lower limit than JPEG.
    if image.width * image.height > (MAX_PIXELS if image.format == "JPEG" else MAX_PIXELS // 2):
        raise TooLarge()
    if image.format == "JPEG":
        # JPEG decodes at 1/2, 1/4 or 1/8 directly. Only the long side of the part that is read must keep MAX_SIDE
        # pixels, so a 48 MP photo is never decoded at full size (both copies below would not fit the memory cap).
        w, h = image.size
        long_side = max((crop[2] - crop[0]) * w, (crop[3] - crop[1]) * h) if crop else max(w, h)
        scale = max(1.0, long_side / MAX_SIDE)
        image.draft("RGB", (math.ceil(w / scale), math.ceil(h / scale)))
    # The crop is in the orientation the user saw, so the EXIF rotation is applied first, in place (no copy).
    ImageOps.exif_transpose(image, in_place=True)
    if image.mode != "RGB":
        image = image.convert("RGB")
    if crop:
        w, h = image.size
        image = image.crop((round(crop[0] * w), round(crop[1] * h), round(crop[2] * w), round(crop[3] * h)))
    image.thumbnail((MAX_SIDE, MAX_SIDE))
    return image


def read_lines(engine, image: Image.Image) -> list[dict]:
    result = engine.predict(np.array(image))[0]
    lines = []
    for poly, text, score in zip(result["rec_polys"], result["rec_texts"], result["rec_scores"]):
        p = np.asarray(poly)
        box = [int(p[:, 0].min()), int(p[:, 1].min()), int(p[:, 0].max()), int(p[:, 1].max())]
        lines.append({"text": text, "score": round(float(score), 4), "box": box})
    return sorted(lines, key=lambda line: (line["box"][1], line["box"][0]))


def memory_mb() -> tuple[float, float]:
    """(process memory, container peak) in MB. Process memory is the cgroup's anonymous memory: the models and buffers,
    what the cap really has to hold. The peak (cgroup v2 memory.peak) also counts page cache the kernel can reclaim, so it
    overstates. Outside a cgroup v2 container both are this process's peak RSS."""
    try:
        with open("/sys/fs/cgroup/memory.stat") as stat, open("/sys/fs/cgroup/memory.peak") as peak:
            anon = next(int(line.split()[1]) for line in stat if line.startswith("anon "))
            return anon / 2**20, int(peak.read()) / 2**20
    except (OSError, StopIteration, ValueError):
        rss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024
        return rss, rss


class Handler(BaseHTTPRequestHandler):
    engine = None
    # Socket timeout: a client that stalls while sending must not block this single-threaded server for good.
    timeout = 20

    def _send(self, status: int, body: dict):
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if urlparse(self.path).path != "/health":
            return self._send(404, {"error": "not found"})
        rss, peak = memory_mb()
        self._send(200, {"status": "ok", "processMb": round(rss), "peakMb": round(peak)})

    def do_POST(self):
        url = urlparse(self.path)
        if url.path != "/recognize":
            return self._send(404, {"error": "not found"})
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length <= 0 or length > MAX_BYTES:
            return self._send(413 if length > MAX_BYTES else 400, {"error": "image body required, at most %d bytes" % MAX_BYTES})
        data = self.rfile.read(length)
        started = time.perf_counter()
        try:
            image = prepare(data, parse_crop(parse_qs(url.query).get("crop", [None])[0]))
        except TooLarge:
            return self._send(413, {"error": "image larger than %d pixels" % MAX_PIXELS})
        except Exception as exc:  # bad image or crop: the caller treats it as "not read"
            return self._send(400, {"error": f"unreadable image: {type(exc).__name__}"})
        try:
            lines = read_lines(self.engine, image)
        except Exception:
            log.exception("OCR failed")
            return self._send(500, {"error": "ocr failed"})
        ms = round((time.perf_counter() - started) * 1000)
        log.info("read %dx%d in %d ms: %d lines", image.width, image.height, ms, len(lines))
        self._send(200, {"width": image.width, "height": image.height, "lines": lines, "ms": ms})

    def log_message(self, fmt, *args):  # request lines go through logging, without the body
        log.info("%s %s", self.command, self.path.split("?")[0])


def main():
    Handler.engine = load_engine()
    rss, peak = memory_mb()
    log.info("models loaded (process %.0f MB, container peak %.0f MB), max side %d, listening on %d", rss, peak, MAX_SIDE, PORT)
    HTTPServer(("0.0.0.0", PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
