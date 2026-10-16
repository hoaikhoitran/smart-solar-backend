"""Ingestion: download → integrity check → extract → chunk → embed → atomic replace of the version's chunks."""

from __future__ import annotations

import hashlib
import time
from dataclasses import dataclass
from typing import Callable, List, Optional

from .chunking import chunk_sections
from .config import SUPPORTED_CONTENT_TYPES, Settings
from .embeddings import Embedder, ProviderError
from .extraction import READY, UNSUPPORTED_CONTENT, extract
from .storage import ObjectStorage, ObjectTooLarge, StorageError
from .vectorstore import EmbeddingDimensionError, StoredChunk, VectorStore

FAILED = "FAILED"  # transient: the API retries with backoff


@dataclass
class IngestOutcome:
    status: str
    page_count: Optional[int] = None
    chunk_count: Optional[int] = None
    characters: Optional[int] = None
    embedding_model: Optional[str] = None
    embedding_dimension: Optional[int] = None
    error: Optional[str] = None


def ingest(settings: Settings, storage: ObjectStorage, embedder: Embedder, store: VectorStore, request: dict,
           clock: Callable[[], float] = time.monotonic) -> IngestOutcome:
    """Runs within settings.ingest_deadline_seconds; past it, stops and reports FAILED so the API retries
    later instead of this process working on after the caller timed out."""
    deadline = clock() + settings.ingest_deadline_seconds
    version_id = str(request["versionId"])
    document_id = str(request["documentId"])
    content_type = str(request.get("contentType") or "")
    if content_type not in SUPPORTED_CONTENT_TYPES:
        return IngestOutcome(UNSUPPORTED_CONTENT, error="unsupported content type")
    try:
        data = storage.read(str(request["bucket"]), str(request["objectKey"]), request.get("objectVersionId"), settings.max_document_bytes)
    except ObjectTooLarge:
        return IngestOutcome(UNSUPPORTED_CONTENT, error="document exceeds the size limit")
    except StorageError as exc:
        return IngestOutcome(FAILED, error=f"storage unavailable ({exc})")

    expected = str(request.get("expectedSha256") or "").lower()
    if hashlib.sha256(data).hexdigest() != expected:
        # The object changed after verification: never index it.
        return IngestOutcome("INTEGRITY_FAILED", error="object checksum does not match the verified file")

    if clock() > deadline:
        return IngestOutcome(FAILED, error="ingest deadline exceeded")
    extraction = extract(data, content_type, settings.max_pages, settings.max_characters, settings.max_uncompressed_bytes)
    if extraction.status != READY:
        return IngestOutcome(extraction.status, page_count=extraction.page_count, characters=extraction.characters, error=extraction.error)

    chunks = chunk_sections(extraction.sections, settings.chunk_characters, settings.chunk_overlap)
    if not chunks:
        return IngestOutcome(UNSUPPORTED_CONTENT, error="no indexable text")
    if len(chunks) > settings.max_chunks:
        return IngestOutcome(UNSUPPORTED_CONTENT, error=f"more than {settings.max_chunks} chunks")
    vectors: List[List[float]] = []
    batch = max(1, settings.embedding_batch_size)
    try:
        for start in range(0, len(chunks), batch):
            if clock() > deadline:
                return IngestOutcome(FAILED, error="ingest deadline exceeded")
            vectors.extend(embedder.embed([c.text for c in chunks[start:start + batch]]))
    except ProviderError as exc:
        return IngestOutcome(FAILED, error=str(exc))
    if clock() > deadline:
        return IngestOutcome(FAILED, error="ingest deadline exceeded")

    stored = [
        StoredChunk(
            chunk_id=f"{version_id.replace('-', '')}:{c.index}",
            version_id=version_id,
            document_id=document_id,
            chunk_index=c.index,
            page=c.page,
            section=c.section,
            content=c.text,
            content_sha256=hashlib.sha256(c.text.encode("utf-8")).hexdigest(),
            embedding=v,
            embedding_model=embedder.model,
        )
        for c, v in zip(chunks, vectors)
    ]
    try:
        store.replace_version(version_id, stored)
    except EmbeddingDimensionError as exc:
        return IngestOutcome(FAILED, error=f"embedding does not fit the vector schema ({exc})")
    except Exception as exc:  # noqa: BLE001 - database outage is transient
        return IngestOutcome(FAILED, error=f"vector store unavailable ({type(exc).__name__})")
    return IngestOutcome(READY, extraction.page_count, len(stored), extraction.characters, embedder.model, embedder.dimension)
