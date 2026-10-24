# The optional StructaDoc layout inference worker (ADR-0013). This image is
# entirely separate from the main Host image, which deliberately contains no
# Python: nothing here is ever added to the root Dockerfile.

FROM python:3.12-slim

# The pinned environment is uv.lock; uv itself is pinned so the lock is
# applied by the same tool version that produced it.
ARG UV_VERSION=0.13.0
RUN pip install --no-cache-dir "uv==${UV_VERSION}"

WORKDIR /app

# Dependencies first, so source changes do not invalidate the install layer.
COPY pyproject.toml uv.lock ./
COPY structadoc_layout_worker ./structadoc_layout_worker
COPY download_models.py README.md ./
RUN uv sync --locked --no-dev --group engine

# Non-root, like every shipped image. The model cache is mounted read-only by
# the deployment; the worker itself writes nothing to disk.
RUN useradd --create-home --uid 10001 layoutworker
USER layoutworker

ENV LAYOUT_WORKER_HOST=0.0.0.0 \
    LAYOUT_WORKER_PORT=8080

EXPOSE 8080
STOPSIGNAL SIGTERM

HEALTHCHECK --interval=30s --timeout=5s --start-period=600s --retries=3 \
    CMD ["/app/.venv/bin/python", "-c", "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8080/health/ready', timeout=4).status == 200 else 1)"]

ENTRYPOINT ["/app/.venv/bin/python", "-m", "structadoc_layout_worker"]
