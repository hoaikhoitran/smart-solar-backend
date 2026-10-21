# SmartSolar internal RAG service (FastAPI). Never publish its port publicly; the API reaches it on
# the internal Docker network and signs every request.
FROM python:3.12-slim AS runtime

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app

RUN useradd --system --uid 10001 --no-create-home rag && chown -R rag /app
USER rag

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/health/live', timeout=3).status == 200 else 1)"

# RAG_WORKERS > 1 requires RAG_VECTOR_STORE=pgvector (the in-memory store is per process).
ENV RAG_WORKERS=1
CMD ["sh", "-c", "exec uvicorn app.main:app_factory --factory --host 0.0.0.0 --port 8000 --workers ${RAG_WORKERS} --no-server-header"]
