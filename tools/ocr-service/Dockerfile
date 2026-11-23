# OMTS OCR sidecar: PaddleOCR 3.2 (PP-OCRv5 mobile det + latin rec), CPU only. Models are fetched at build time, so the
# container runs offline. Build: docker build -t omts-ocr .
FROM python:3.11-slim-bookworm

RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 libgl1 libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir paddlepaddle==3.2.0 paddleocr==3.2.0

RUN useradd --create-home --uid 10001 ocr
USER ocr
WORKDIR /home/ocr
ENV PYTHONUNBUFFERED=1 \
    PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK=True \
    OMP_NUM_THREADS=2 \
    OCR_CPU_THREADS=2

COPY --chown=ocr:ocr app.py warmup.py ./
# Downloads both models into ~/.paddlex and checks they answer.
RUN python warmup.py

EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4)"
CMD ["python", "app.py"]
