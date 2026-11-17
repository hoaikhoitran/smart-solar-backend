"""Embedding providers.

* ``openai`` (production): OpenAI ``text-embedding-3-small`` with the configured dimension (1536 native, or 1024 through the
  API's ``dimensions`` parameter) requested explicitly. Every response is checked (count, model, dimension, finite values)
  before anything is stored; a provider failure is reported, never answered by another model. Token usage is reported
  so the API can charge it to its cost ledger.
* ``ollama`` (production, local inference): Ollama's native ``/api/embed`` (no API key), e.g.
  ``qwen3-embedding:0.6b`` at its native 1024 dimensions. Same checks; ``truncate`` is off, so a text longer
  than the model's context fails instead of being embedded partially. Not billed.
* ``hashing`` (development/test only, refused in production by config validation): deterministic and
  offline, with no retrieval quality guarantees; its vectors carry their own model name.

Chunks are stored with ``embedder.model`` and retrieval filters on the query embedder's model, so vectors of different models
never mix. During a model migration the ingestion embedder (new model) and the query embedder (old model) may differ.
"""

from __future__ import annotations

import hashlib
import math
import re
import unicodedata
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Protocol, Tuple

import httpx

from .config import Settings


class ProviderError(Exception):
    """Provider failure (timeout, transport error, HTTP error): the ingestion is retried later by the API.

    ``billing`` tells the API whether the failed call may still have been charged: ``none`` (refused before any work: an HTTP error
    response, a connection that never opened) or ``uncertain`` (the request may have been processed: a timeout, a dropped
    connection, an unreadable answer).
    """

    billing = "none"


class ProviderTimeout(ProviderError):
    """The provider did not answer within the time given (the client timeout or the caller's deadline): it may have billed."""

    billing = "uncertain"


class ProviderRateLimited(ProviderError):
    """HTTP 429 from the provider (rate or quota limit). Never retried with another model; not billed."""


class EmbeddingContractError(ProviderError):
    """The provider answered, but not with what was asked for (model, dimension, count or values). It answered, so it may have billed."""

    billing = "uncertain"


def transport_error(exc: httpx.HTTPError, what: str) -> ProviderError:
    """Timeouts and dropped connections may have been processed (billed); a connection that never opened was not."""
    if isinstance(exc, httpx.TimeoutException):
        return ProviderTimeout(f"{what} timed out")
    error = ProviderError(f"{what} unreachable ({type(exc).__name__})")
    if not isinstance(exc, httpx.ConnectError):
        error.billing = "uncertain"
    return error


def status_error(status: int, what: str) -> ProviderError:
    # The body is not echoed: it can contain request details.
    if status == 429:
        return ProviderRateLimited(f"{what} rate limited (HTTP 429)")
    return ProviderError(f"{what} returned HTTP {status}")


@dataclass(frozen=True)
class EmbeddingResult:
    """Vectors plus the input tokens charged (0 for local or offline providers)."""

    vectors: List[List[float]]
    tokens: int


def estimate_embedding_tokens(texts: List[str]) -> int:
    """Conservative estimate (Vietnamese: about 3 characters per token) when a billable provider reports no usage."""
    return sum(max(1, len(t) // 3) for t in texts)


class Embedder(Protocol):
    model: str
    dimension: int
    # "openai" (billed), "ollama" (local) or "hashing" (offline stand-in): the API only charges billable providers.
    provider: str

    def embed(self, texts: List[str]) -> List[List[float]]: ...

    def embed_with_usage(self, texts: List[str]) -> EmbeddingResult: ...


_WORD = re.compile(r"\w+", re.UNICODE)


def _fold(text: str) -> str:
    # Vietnamese diacritics folded so "bảo hành" and "bao hanh" match in the dev embedder.
    decomposed = unicodedata.normalize("NFKD", text.lower().replace("đ", "d"))
    return "".join(c for c in decomposed if not unicodedata.combining(c))


class HashingEmbedder:
    provider = "hashing"

    def __init__(self, dimension: int, model: str = "hashing-dev") -> None:
        self.dimension = dimension
        self.model = model

    def embed(self, texts: List[str]) -> List[List[float]]:
        return [self._one(t) for t in texts]

    def embed_with_usage(self, texts: List[str]) -> EmbeddingResult:
        return EmbeddingResult(self.embed(texts), 0)

    def _one(self, text: str) -> List[float]:
        vector = [0.0] * self.dimension
        words = _WORD.findall(_fold(text))
        features = words + [f"{a}_{b}" for a, b in zip(words, words[1:])]
        for feature in features:
            digest = hashlib.blake2b(feature.encode("utf-8"), digest_size=8).digest()
            bucket = int.from_bytes(digest[:4], "little") % self.dimension
            vector[bucket] += 1.0 if digest[4] & 1 else -1.0
        norm = math.sqrt(sum(v * v for v in vector)) or 1.0
        return [v / norm for v in vector]


class OpenAIEmbedder:
    """OpenAI /v1/embeddings. Never falls back to another model or provider."""

    provider = "openai"

    def __init__(self, settings: Settings, transport: Optional[httpx.BaseTransport] = None) -> None:
        self.model = settings.embedding_model
        self.dimension = settings.embedding_dimension
        self._batch = max(1, settings.embedding_batch_size)
        self._client = httpx.Client(
            base_url=(settings.embedding_base_url or "").rstrip("/") + "/",
            headers={"Authorization": f"Bearer {settings.embedding_api_key}"},
            timeout=settings.provider_timeout_seconds,
            transport=transport,
        )

    def embed(self, texts: List[str]) -> List[List[float]]:
        return self.embed_with_usage(texts).vectors

    def embed_with_usage(self, texts: List[str]) -> EmbeddingResult:
        vectors: List[List[float]] = []
        tokens = 0
        for start in range(0, len(texts), self._batch):
            batch = texts[start:start + self._batch]
            batch_vectors, batch_tokens = self._embed_batch(batch)
            vectors.extend(batch_vectors)
            tokens += batch_tokens if batch_tokens is not None else estimate_embedding_tokens(batch)
        return EmbeddingResult(vectors, tokens)

    def _embed_batch(self, batch: List[str]) -> Tuple[List[List[float]], Optional[int]]:
        payload = {"model": self.model, "input": batch, "dimensions": self.dimension, "encoding_format": "float"}
        try:
            response = self._client.post("embeddings", json=payload)
        except httpx.HTTPError as exc:
            raise transport_error(exc, "embedding provider") from exc
        if response.status_code >= 400:
            raise status_error(response.status_code, "embedding provider")
        try:
            body = response.json()
        except ValueError as exc:
            raise EmbeddingContractError("embedding provider returned a non-JSON body") from exc

        returned_model = str(body.get("model") or "")
        if returned_model and not returned_model.startswith(self.model):
            raise EmbeddingContractError(f"embedding provider answered with model {returned_model!r}, expected {self.model!r}")
        data = sorted(body.get("data") or [], key=lambda d: d.get("index", 0))
        if len(data) != len(batch):
            raise EmbeddingContractError("embedding provider returned an unexpected number of vectors")
        vectors = []
        for item in data:
            vector = item.get("embedding")
            if not isinstance(vector, list) or len(vector) != self.dimension:
                raise EmbeddingContractError(f"embedding has {len(vector) if isinstance(vector, list) else 'no'} dimensions, expected {self.dimension}")
            try:
                values = [float(v) for v in vector]
            except (TypeError, ValueError) as exc:
                raise EmbeddingContractError("embedding contains non-numeric values") from exc
            if not all(math.isfinite(v) for v in values):
                raise EmbeddingContractError("embedding contains non-finite values")
            vectors.append(values)
        usage = body.get("usage") if isinstance(body.get("usage"), dict) else {}
        reported = usage.get("prompt_tokens", usage.get("total_tokens"))
        tokens = reported if isinstance(reported, int) and not isinstance(reported, bool) and reported >= 0 else None
        return vectors, tokens


# Backwards-compatible name.
OpenAICompatibleEmbedder = OpenAIEmbedder


def check_vectors(raw: object, count: int, dimension: int) -> List[List[float]]:
    """The provider's vectors, or EmbeddingContractError: exact count, exact dimension, finite numbers only."""
    if not isinstance(raw, list) or len(raw) != count:
        raise EmbeddingContractError("embedding provider returned an unexpected number of vectors")
    vectors = []
    for vector in raw:
        if not isinstance(vector, list) or len(vector) != dimension:
            raise EmbeddingContractError(f"embedding has {len(vector) if isinstance(vector, list) else 'no'} dimensions, expected {dimension}")
        try:
            values = [float(v) for v in vector]
        except (TypeError, ValueError) as exc:
            raise EmbeddingContractError("embedding contains non-numeric values") from exc
        if not all(math.isfinite(v) for v in values):
            raise EmbeddingContractError("embedding contains non-finite values")
        vectors.append(values)
    return vectors


def ollama_model_available(client: httpx.Client, model: str) -> bool:
    """Whether the Ollama server has the model pulled (GET /api/tags). False on any error: readiness reports, never raises."""
    try:
        response = client.get("api/tags", timeout=3)
        names = {str(m.get(key) or "") for m in (response.json().get("models") or []) for key in ("name", "model")} if response.status_code == 200 else set()
    except Exception:  # noqa: BLE001 - readiness must report, not raise
        return False
    return model in names or (":" not in model and f"{model}:latest" in names)


def ollama_options(settings: Settings) -> Dict[str, Any]:
    return {"num_thread": settings.ollama_num_thread} if settings.ollama_num_thread else {}


class OllamaEmbedder:
    """Ollama /api/embed (local inference, no API key). Never falls back to another model or provider."""

    provider = "ollama"

    def __init__(self, settings: Settings, transport: Optional[httpx.BaseTransport] = None) -> None:
        self.model = settings.embedding_model
        self.dimension = settings.embedding_dimension
        self._batch = max(1, settings.embedding_batch_size)
        self._keep_alive = settings.ollama_keep_alive
        self._options = ollama_options(settings)
        self._client = httpx.Client(
            base_url=(settings.embedding_base_url or "").rstrip("/") + "/",
            timeout=settings.provider_timeout_seconds,
            transport=transport,
        )

    def embed(self, texts: List[str]) -> List[List[float]]:
        vectors: List[List[float]] = []
        for start in range(0, len(texts), self._batch):
            vectors.extend(self._embed_batch(texts[start:start + self._batch]))
        return vectors

    def embed_with_usage(self, texts: List[str]) -> EmbeddingResult:
        # Local inference is not billed: nothing is charged to the cost ledger.
        return EmbeddingResult(self.embed(texts), 0)

    def _embed_batch(self, batch: List[str]) -> List[List[float]]:
        # No `dimensions`: the model's native size is used and checked; it is never truncated to fit a schema.
        payload: Dict[str, Any] = {"model": self.model, "input": batch, "truncate": False, "keep_alive": self._keep_alive}
        if self._options:
            payload["options"] = self._options
        try:
            response = self._client.post("api/embed", json=payload)
        except httpx.HTTPError as exc:
            raise transport_error(exc, "embedding provider") from exc
        if response.status_code >= 400:
            raise status_error(response.status_code, "embedding provider")
        try:
            body = response.json()
        except ValueError as exc:
            raise EmbeddingContractError("embedding provider returned a non-JSON body") from exc
        if not isinstance(body, dict):
            raise EmbeddingContractError("embedding provider returned an unexpected body")
        returned_model = str(body.get("model") or "")
        if returned_model and returned_model != self.model:
            raise EmbeddingContractError(f"embedding provider answered with model {returned_model!r}, expected {self.model!r}")
        return check_vectors(body.get("embeddings"), len(batch), self.dimension)

    def available(self) -> bool:
        return ollama_model_available(self._client, self.model)


def embed_counted(embedder: Embedder, texts: List[str]) -> EmbeddingResult:
    """Vectors plus billable tokens from any embedder (one implementing only ``embed`` reports 0 tokens)."""
    counted = getattr(embedder, "embed_with_usage", None)
    return counted(texts) if counted is not None else EmbeddingResult(embedder.embed(texts), 0)


def create_embedder(settings: Settings) -> Embedder:
    """The configured provider only (validated beforehand); there is no fallback between providers."""
    if settings.embedding_provider == "openai":
        return OpenAIEmbedder(settings)
    if settings.embedding_provider == "ollama":
        return OllamaEmbedder(settings)
    if settings.embedding_provider == "hashing":
        return HashingEmbedder(settings.embedding_dimension, settings.embedding_model)
    raise ValueError("unknown embedding provider")


def create_query_embedder(settings: Settings, ingest_embedder: Embedder) -> Embedder:
    """The embedder for questions: the ingestion embedder, unless a separate query embedder is configured (model migration)."""
    return create_embedder(settings.query_settings()) if settings.has_query_embedder() else ingest_embedder
