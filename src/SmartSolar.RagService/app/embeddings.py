"""Embedding providers.

* ``openai`` (production): OpenAI ``text-embedding-3-small``, 1536 dimensions requested explicitly. Every
  response is checked (count, model, dimension, finite values) before anything is stored; a provider
  failure is reported, never answered by another model.
* ``hashing`` (development/test only, refused in production by config validation): deterministic and
  offline, with no retrieval quality guarantees; its vectors carry their own model name.

Documents and queries always go through the same configured embedder, and chunks are stored with
``embedder.model``; retrieval filters on it, so vectors of different models never mix.
"""

from __future__ import annotations

import hashlib
import math
import re
import unicodedata
from typing import List, Optional, Protocol

import httpx

from .config import Settings


class ProviderError(Exception):
    """Provider failure (timeout, transport error, HTTP error): the ingestion is retried later by the API."""


class EmbeddingContractError(ProviderError):
    """The provider answered, but not with what was asked for (model, dimension, count or values)."""


class Embedder(Protocol):
    model: str
    dimension: int

    def embed(self, texts: List[str]) -> List[List[float]]: ...


_WORD = re.compile(r"\w+", re.UNICODE)


def _fold(text: str) -> str:
    # Vietnamese diacritics folded so "bảo hành" and "bao hanh" match in the dev embedder.
    decomposed = unicodedata.normalize("NFKD", text.lower().replace("đ", "d"))
    return "".join(c for c in decomposed if not unicodedata.combining(c))


class HashingEmbedder:
    def __init__(self, dimension: int, model: str = "hashing-dev") -> None:
        self.dimension = dimension
        self.model = model

    def embed(self, texts: List[str]) -> List[List[float]]:
        return [self._one(t) for t in texts]

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
        vectors: List[List[float]] = []
        for start in range(0, len(texts), self._batch):
            vectors.extend(self._embed_batch(texts[start:start + self._batch]))
        return vectors

    def _embed_batch(self, batch: List[str]) -> List[List[float]]:
        payload = {"model": self.model, "input": batch, "dimensions": self.dimension, "encoding_format": "float"}
        try:
            response = self._client.post("embeddings", json=payload)
        except httpx.TimeoutException as exc:
            raise ProviderError("embedding provider timed out") from exc
        except httpx.HTTPError as exc:
            raise ProviderError(f"embedding provider unreachable ({type(exc).__name__})") from exc
        if response.status_code >= 400:
            # The body is not echoed: it can contain request details.
            raise ProviderError(f"embedding provider returned HTTP {response.status_code}")
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
        return vectors


# Backwards-compatible name.
OpenAICompatibleEmbedder = OpenAIEmbedder


def create_embedder(settings: Settings) -> Embedder:
    """The configured provider only (validated beforehand); there is no fallback between providers."""
    if settings.embedding_provider == "openai":
        return OpenAIEmbedder(settings)
    if settings.embedding_provider == "hashing":
        return HashingEmbedder(settings.embedding_dimension, settings.embedding_model)
    raise ValueError("unknown embedding provider")
