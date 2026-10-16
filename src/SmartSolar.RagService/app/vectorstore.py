"""Chunk storage and similarity search.

pgvector: the knowledge_chunk table is defined in sql/knowledge_chunk.sql and must be created by an
approved manual migration (this service never creates or alters schema). Replacing a version's
chunks is a single transaction (delete + insert), so retried ingestion never duplicates chunks.
"""

from __future__ import annotations

import math
import re
import threading
from dataclasses import dataclass
from typing import Dict, List, Optional, Protocol, Sequence


@dataclass
class StoredChunk:
    chunk_id: str
    version_id: str
    document_id: str
    chunk_index: int
    page: Optional[int]
    section: Optional[str]
    content: str
    content_sha256: str
    embedding: List[float]
    embedding_model: str


@dataclass
class SearchHit:
    chunk_id: str
    version_id: str
    document_id: str
    page: Optional[int]
    section: Optional[str]
    content: str
    score: float


class VectorStore(Protocol):
    def replace_version(self, version_id: str, chunks: Sequence[StoredChunk]) -> None: ...

    def delete_version(self, version_id: str) -> int: ...

    def search(self, embedding: List[float], version_ids: Sequence[str], top_k: int, embedding_model: str) -> List[SearchHit]: ...

    def ready(self) -> bool: ...


def _cosine(a: List[float], b: List[float]) -> float:
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a)) or 1.0
    nb = math.sqrt(sum(y * y for y in b)) or 1.0
    return dot / (na * nb)


class EmbeddingDimensionError(ValueError):
    """A vector does not have the dimension of the store (would corrupt or fail the vector column)."""


def check_chunks(chunks: Sequence[StoredChunk], dimension: Optional[int]) -> None:
    """Rejects a batch before anything is written: wrong dimension or more than one embedding model."""
    if len({c.embedding_model for c in chunks}) > 1:
        raise EmbeddingDimensionError("all chunks of a version must use one embedding model")
    for c in chunks:
        if dimension is not None and len(c.embedding) != dimension:
            raise EmbeddingDimensionError(f"chunk {c.chunk_id} has {len(c.embedding)} dimensions, expected {dimension}")


def check_query(embedding: List[float], dimension: Optional[int]) -> None:
    if dimension is not None and len(embedding) != dimension:
        raise EmbeddingDimensionError(f"query embedding has {len(embedding)} dimensions, expected {dimension}")


class InMemoryVectorStore:
    """Development/test only (not durable, single process)."""

    def __init__(self, dimension: Optional[int] = None) -> None:
        self._chunks: Dict[str, List[StoredChunk]] = {}
        self._lock = threading.Lock()
        self._dimension = dimension

    def replace_version(self, version_id: str, chunks: Sequence[StoredChunk]) -> None:
        check_chunks(chunks, self._dimension)
        with self._lock:
            self._chunks[version_id] = list(chunks)

    def delete_version(self, version_id: str) -> int:
        with self._lock:
            return len(self._chunks.pop(version_id, []))

    def search(self, embedding: List[float], version_ids: Sequence[str], top_k: int, embedding_model: str) -> List[SearchHit]:
        check_query(embedding, self._dimension)
        with self._lock:
            candidates = [c for v in version_ids for c in self._chunks.get(v, []) if c.embedding_model == embedding_model]
        scored = sorted(((c, _cosine(embedding, c.embedding)) for c in candidates), key=lambda x: x[1], reverse=True)[:top_k]
        return [SearchHit(c.chunk_id, c.version_id, c.document_id, c.page, c.section, c.content, s) for c, s in scored]

    def ready(self) -> bool:
        return True

    def count(self, version_id: str) -> int:
        return len(self._chunks.get(version_id, []))


def _vector_literal(values: List[float]) -> str:
    return "[" + ",".join(f"{v:.7g}" for v in values) + "]"


class PgVectorStore:
    def __init__(self, database_url: str, dimension: int = 1536) -> None:
        from psycopg_pool import ConnectionPool  # type: ignore[import-not-found]

        self._pool = ConnectionPool(database_url, min_size=1, max_size=10, kwargs={"autocommit": False}, open=True)
        self._dimension = dimension

    def replace_version(self, version_id: str, chunks: Sequence[StoredChunk]) -> None:
        check_chunks(chunks, self._dimension)
        with self._pool.connection() as conn, conn.transaction(), conn.cursor() as cur:
            # Serialise concurrent ingests of one version (a retry overlapping a slow first run).
            cur.execute("SELECT pg_advisory_xact_lock(hashtextextended(%s, 0))", ("knowledge_chunk:" + version_id,))
            cur.execute("DELETE FROM knowledge_chunk WHERE version_id = %s", (version_id,))
            cur.executemany(
                "INSERT INTO knowledge_chunk (chunk_id, version_id, document_id, chunk_index, page_number, section, content, content_sha256,"
                " embedding, embedding_model) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s::vector, %s)",
                [
                    (c.chunk_id, c.version_id, c.document_id, c.chunk_index, c.page, c.section, c.content, c.content_sha256,
                     _vector_literal(c.embedding), c.embedding_model)
                    for c in chunks
                ],
            )

    def delete_version(self, version_id: str) -> int:
        with self._pool.connection() as conn, conn.transaction(), conn.cursor() as cur:
            cur.execute("DELETE FROM knowledge_chunk WHERE version_id = %s", (version_id,))
            return cur.rowcount

    def search(self, embedding: List[float], version_ids: Sequence[str], top_k: int, embedding_model: str) -> List[SearchHit]:
        if not version_ids:
            return []
        check_query(embedding, self._dimension)
        with self._pool.connection() as conn, conn.transaction(), conn.cursor() as cur:
            # Filtered HNSW search: widen the candidate list so the version filter still yields top_k rows.
            cur.execute("SET LOCAL hnsw.ef_search = 200")
            cur.execute(
                "SELECT chunk_id, version_id::text, document_id::text, page_number, section, content, 1 - (embedding <=> %s::vector) AS score"
                " FROM knowledge_chunk WHERE version_id = ANY(%s::uuid[]) AND embedding_model = %s"
                " ORDER BY embedding <=> %s::vector LIMIT %s",
                (_vector_literal(embedding), list(version_ids), embedding_model, _vector_literal(embedding), top_k),
            )
            return [SearchHit(r[0], r[1], r[2], r[3], r[4], r[5], float(r[6])) for r in cur.fetchall()]

    def schema_dimension(self) -> Optional[int]:
        """Dimension of knowledge_chunk.embedding (vector(n)), or None if the table/column is missing."""
        with self._pool.connection(timeout=3) as conn, conn.cursor() as cur:
            cur.execute(
                "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a"
                " WHERE a.attrelid = to_regclass('knowledge_chunk') AND a.attname = 'embedding' AND NOT a.attisdropped")
            row = cur.fetchone()
        match = re.fullmatch(r"(?:\w+\.)?vector\((\d+)\)", row[0]) if row and row[0] else None
        return int(match.group(1)) if match else None

    def ready(self) -> bool:
        """The table exists and its vector column has the configured dimension."""
        try:
            return self.schema_dimension() == self._dimension
        except Exception:  # noqa: BLE001 - readiness must report, not raise
            return False


def create_vector_store(kind: str, database_url: Optional[str], dimension: int) -> VectorStore:
    if kind == "pgvector":
        return PgVectorStore(database_url or "", dimension)
    return InMemoryVectorStore(dimension)
