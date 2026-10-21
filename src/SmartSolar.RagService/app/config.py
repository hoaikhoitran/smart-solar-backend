"""Configuration from environment variables only. Secret values are never logged or echoed."""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from typing import List, Optional

PDF = "application/pdf"
DOCX = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
TEXT = "text/plain"
MARKDOWN = "text/markdown"
SUPPORTED_CONTENT_TYPES = (PDF, DOCX, TEXT, MARKDOWN)

# knowledge_chunk.embedding is vector(1536) (sql/knowledge_chunk.sql). Changing it is a schema change
# plus a full reindex; the service refuses any configuration that does not produce exactly this size.
SCHEMA_EMBEDDING_DIMENSION = 1536

# Production embedding models and the dimension each must return (requested explicitly via `dimensions`).
PRODUCTION_EMBEDDING_MODELS = {"text-embedding-3-small": 1536}
DEFAULT_EMBEDDING_MODEL = "text-embedding-3-small"
DEFAULT_OPENAI_BASE_URL = "https://api.openai.com/v1"

# Offline stand-in for development and tests only; its vectors are tagged with this model name, so they
# can never be retrieved by (or mixed with) real embeddings.
DEV_EMBEDDING_MODEL = "hashing-dev"
NON_PRODUCTION_ENVIRONMENTS = ("development", "test")

# Per-request generation and chunking values (sent by the API from the admin RAG configuration) are validated against these.
MODEL_ID_PATTERN = re.compile(r"^[A-Za-z0-9._:-]{1,100}$")
TOKEN_PARAMETERS = ("max_tokens", "max_completion_tokens")
MIN_CHUNK_CHARACTERS = 400
MAX_CHUNK_CHARACTERS = 8000


def _int(name: str, default: int) -> int:
    raw = os.environ.get(name)
    return int(raw) if raw not in (None, "") else default


def _float(name: str, default: float) -> float:
    raw = os.environ.get(name)
    return float(raw) if raw not in (None, "") else default


def _str(name: str, default: Optional[str] = None) -> Optional[str]:
    raw = os.environ.get(name)
    return raw if raw not in (None, "") else default


def _list(name: str) -> List[str]:
    return [x.strip() for x in (os.environ.get(name) or "").split(",") if x.strip()]


@dataclass
class Settings:
    environment: str = "development"
    shared_secret: str = ""
    signature_window_seconds: int = 300
    max_request_bytes: int = 256 * 1024

    vector_store: str = "memory"  # pgvector | memory
    database_url: Optional[str] = None

    storage: str = "s3"  # s3 | local
    s3_region: Optional[str] = None
    s3_endpoint_url: Optional[str] = None
    s3_allowed_buckets: List[str] = field(default_factory=list)
    local_storage_dir: str = "/data/objects"

    embedding_provider: str = "hashing"  # openai | hashing
    embedding_model: str = "hashing-dev"
    embedding_dimension: int = 1536
    embedding_base_url: Optional[str] = None
    embedding_api_key: Optional[str] = None
    embedding_batch_size: int = 64

    llm_provider: str = "extractive"  # openai | extractive
    llm_model: str = "extractive-dev"
    llm_base_url: Optional[str] = None
    llm_api_key: Optional[str] = None
    llm_temperature: float = 0.1
    # Optional allowlist of generation models a request may select (empty = any id matching MODEL_ID_PATTERN).
    llm_allowed_models: List[str] = field(default_factory=list)
    # Prompt template used when a request names none (see prompting.PROMPT_TEMPLATES).
    prompt_version: str = "v1"

    provider_timeout_seconds: int = 45
    # Must stay below the API's Rag:IngestTimeoutSeconds (300): stop and report FAILED before the caller gives up.
    ingest_deadline_seconds: int = 270
    max_document_bytes: int = 50 * 1024 * 1024
    max_uncompressed_bytes: int = 200 * 1024 * 1024
    max_pages: int = 1000
    max_characters: int = 3_000_000
    max_chunks: int = 5000
    chunk_characters: int = 2400
    chunk_overlap: int = 300
    min_score: float = 0.2
    max_top_k: int = 10
    max_context_tokens: int = 6000
    max_output_tokens: int = 1500
    max_concurrent_generations: int = 8
    max_concurrent_ingestions: int = 2
    queue_wait_seconds: int = 10

    @staticmethod
    def from_env() -> Settings:
        # Production embeddings by default: an unset provider means OpenAI text-embedding-3-small (and a
        # missing API key fails validation) rather than silently using the offline stand-in.
        provider = _str("RAG_EMBEDDING_PROVIDER", "openai") or "openai"
        return Settings(
            environment=_str("RAG_ENV", "development") or "development",
            shared_secret=_str("RAG_SHARED_SECRET", "") or "",
            signature_window_seconds=_int("RAG_SIGNATURE_WINDOW_SECONDS", 300),
            max_request_bytes=_int("RAG_MAX_REQUEST_BYTES", 256 * 1024),
            vector_store=_str("RAG_VECTOR_STORE", "memory") or "memory",
            database_url=_str("RAG_DATABASE_URL"),
            storage=_str("RAG_STORAGE", "s3") or "s3",
            s3_region=_str("RAG_S3_REGION"),
            s3_endpoint_url=_str("RAG_S3_ENDPOINT_URL"),
            s3_allowed_buckets=_list("RAG_S3_ALLOWED_BUCKETS"),
            local_storage_dir=_str("RAG_LOCAL_STORAGE_DIR", "/data/objects") or "/data/objects",
            embedding_provider=provider,
            embedding_model=_str("RAG_EMBEDDING_MODEL") or (DEV_EMBEDDING_MODEL if provider == "hashing" else DEFAULT_EMBEDDING_MODEL),
            embedding_dimension=_int("RAG_EMBEDDING_DIMENSION", SCHEMA_EMBEDDING_DIMENSION),
            embedding_base_url=_str("RAG_EMBEDDING_BASE_URL") or (DEFAULT_OPENAI_BASE_URL if provider == "openai" else None),
            embedding_api_key=_str("RAG_EMBEDDING_API_KEY"),
            embedding_batch_size=_int("RAG_EMBEDDING_BATCH_SIZE", 64),
            llm_provider=_str("RAG_LLM_PROVIDER", "extractive") or "extractive",
            llm_model=_str("RAG_LLM_MODEL", "extractive-dev") or "extractive-dev",
            llm_base_url=_str("RAG_LLM_BASE_URL"),
            llm_api_key=_str("RAG_LLM_API_KEY"),
            llm_temperature=_float("RAG_LLM_TEMPERATURE", 0.1),
            llm_allowed_models=_list("RAG_LLM_ALLOWED_MODELS"),
            prompt_version=_str("RAG_PROMPT_VERSION", "v1") or "v1",
            provider_timeout_seconds=_int("RAG_PROVIDER_TIMEOUT_SECONDS", 45),
            ingest_deadline_seconds=_int("RAG_INGEST_DEADLINE_SECONDS", 270),
            max_document_bytes=_int("RAG_MAX_DOCUMENT_BYTES", 50 * 1024 * 1024),
            max_uncompressed_bytes=_int("RAG_MAX_UNCOMPRESSED_BYTES", 200 * 1024 * 1024),
            max_pages=_int("RAG_MAX_PAGES", 1000),
            max_characters=_int("RAG_MAX_CHARACTERS", 3_000_000),
            max_chunks=_int("RAG_MAX_CHUNKS", 5000),
            chunk_characters=_int("RAG_CHUNK_CHARACTERS", 2400),
            chunk_overlap=_int("RAG_CHUNK_OVERLAP", 300),
            min_score=_float("RAG_MIN_SCORE", 0.2),
            max_top_k=_int("RAG_MAX_TOP_K", 10),
            max_context_tokens=_int("RAG_MAX_CONTEXT_TOKENS", 6000),
            max_output_tokens=_int("RAG_MAX_OUTPUT_TOKENS", 1500),
            max_concurrent_generations=_int("RAG_MAX_CONCURRENT_GENERATIONS", 8),
            max_concurrent_ingestions=_int("RAG_MAX_CONCURRENT_INGESTIONS", 2),
            queue_wait_seconds=_int("RAG_QUEUE_WAIT_SECONDS", 10),
        )

    def validate(self) -> None:
        """Fails fast on unsafe configuration. Messages name keys, never values."""
        problems = []
        if len(self.shared_secret) < 32:
            problems.append("RAG_SHARED_SECRET must be at least 32 characters")
        if self.vector_store not in ("pgvector", "memory"):
            problems.append("RAG_VECTOR_STORE must be pgvector or memory")
        if self.vector_store == "pgvector" and not self.database_url:
            problems.append("RAG_DATABASE_URL is required for pgvector")
        if self.storage not in ("s3", "local"):
            problems.append("RAG_STORAGE must be s3 or local")
        if self.storage == "s3" and not self.s3_allowed_buckets:
            problems.append("RAG_S3_ALLOWED_BUCKETS is required for s3 storage")
        problems.extend(self._embedding_problems())
        if self.llm_provider not in ("openai", "extractive"):
            problems.append("RAG_LLM_PROVIDER must be openai or extractive")
        if self.llm_provider == "openai" and not (self.llm_base_url and self.llm_api_key):
            problems.append("RAG_LLM_BASE_URL and RAG_LLM_API_KEY are required for the openai LLM provider")
        if not 10 <= self.ingest_deadline_seconds <= 1700:
            problems.append("RAG_INGEST_DEADLINE_SECONDS must be between 10 and 1700")
        if self.chunk_overlap >= self.chunk_characters:
            problems.append("RAG_CHUNK_OVERLAP must be smaller than RAG_CHUNK_CHARACTERS")
        if any(not MODEL_ID_PATTERN.match(m) for m in self.llm_allowed_models):
            problems.append("RAG_LLM_ALLOWED_MODELS entries must match ^[A-Za-z0-9._:-]{1,100}$")
        from .prompting import PROMPT_TEMPLATES  # local import: prompting imports llm, which imports this module

        if self.prompt_version not in PROMPT_TEMPLATES:
            problems.append(f"RAG_PROMPT_VERSION must be one of: {', '.join(PROMPT_TEMPLATES)}")
        if self.environment == "production":
            if self.vector_store != "pgvector":
                problems.append("production requires RAG_VECTOR_STORE=pgvector")
            if self.storage != "s3":
                problems.append("production requires RAG_STORAGE=s3")
            if self.embedding_provider == "hashing" or self.llm_provider == "extractive":
                problems.append("production requires real embedding and LLM providers")
        if problems:
            raise ValueError("Invalid RAG service configuration: " + "; ".join(problems))

    def _embedding_problems(self) -> List[str]:
        """Model and dimension must match the vector schema; the offline provider never runs in production."""
        problems = []
        if self.embedding_provider == "openai":
            expected = PRODUCTION_EMBEDDING_MODELS.get(self.embedding_model)
            if expected is None:
                problems.append(f"RAG_EMBEDDING_MODEL must be one of: {', '.join(PRODUCTION_EMBEDDING_MODELS)}")
            elif self.embedding_dimension != expected:
                problems.append(f"RAG_EMBEDDING_DIMENSION must be {expected} for RAG_EMBEDDING_MODEL={self.embedding_model}")
            if not self.embedding_base_url or not self.embedding_api_key:
                problems.append("RAG_EMBEDDING_BASE_URL and RAG_EMBEDDING_API_KEY are required for openai embeddings")
        elif self.embedding_provider == "hashing":
            if self.environment not in NON_PRODUCTION_ENVIRONMENTS:
                problems.append("RAG_EMBEDDING_PROVIDER=hashing is only allowed with RAG_ENV=development or test")
            if self.embedding_model != DEV_EMBEDDING_MODEL:
                problems.append(f"RAG_EMBEDDING_MODEL must be {DEV_EMBEDDING_MODEL} for the hashing provider")
            if not 8 <= self.embedding_dimension <= 4096:
                problems.append("RAG_EMBEDDING_DIMENSION must be between 8 and 4096")
        else:
            problems.append("RAG_EMBEDDING_PROVIDER must be openai or hashing")
        if self.vector_store == "pgvector" and self.embedding_dimension != SCHEMA_EMBEDDING_DIMENSION:
            problems.append(f"RAG_EMBEDDING_DIMENSION must be {SCHEMA_EMBEDDING_DIMENSION} to match knowledge_chunk.embedding")
        return problems
