"""FastAPI entry point. Internal only: never published on a public port; every /internal call is HMAC-signed."""

from __future__ import annotations

import logging
import threading
import uuid
from dataclasses import dataclass
from typing import List, Literal, Optional

import anyio
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, ValidationError, model_validator

from .answering import answer_question
from .config import MAX_CHUNK_CHARACTERS, MIN_CHUNK_CHARACTERS, Settings
from .embeddings import Embedder, ProviderError, create_embedder
from .ingestion import ingest
from .llm import ChatModel, GenerationOptions, create_chat_model
from .prompting import PROMPT_TEMPLATES
from .security import SIGNATURE_HEADER, TIMESTAMP_HEADER, SignatureVerifier
from .storage import ObjectStorage, create_storage
from .vectorstore import EmbeddingDimensionError, VectorStore, create_vector_store

logger = logging.getLogger("smartsolar.rag")


class StrictBody(BaseModel):
    # Unknown fields are rejected (a typo must not silently fall back to a default).
    model_config = ConfigDict(extra="forbid")


class IngestBody(StrictBody):
    versionId: uuid.UUID
    documentId: uuid.UUID
    bucket: str = Field(min_length=1, max_length=255)
    objectKey: str = Field(min_length=1, max_length=1024)
    objectVersionId: Optional[str] = Field(default=None, max_length=1024)
    contentType: str = Field(min_length=1, max_length=200)
    expectedSha256: str = Field(pattern=r"^[0-9a-fA-F]{64}$")
    title: str = Field(default="", max_length=300)
    versionNumber: int = Field(ge=1)
    # Configuration snapshot of the API job; absent values use the RAG_CHUNK_* environment defaults.
    configVersion: Optional[int] = Field(default=None, ge=0)
    chunkCharacters: Optional[int] = Field(default=None, ge=MIN_CHUNK_CHARACTERS, le=MAX_CHUNK_CHARACTERS)
    chunkOverlap: Optional[int] = Field(default=None, ge=0, le=MAX_CHUNK_CHARACTERS)

    @model_validator(mode="after")
    def _overlap_below_size(self) -> IngestBody:
        if self.chunkCharacters is not None and self.chunkOverlap is not None and self.chunkOverlap >= self.chunkCharacters:
            raise ValueError("chunkOverlap must be smaller than chunkCharacters")
        return self


class DeleteBody(StrictBody):
    versionId: uuid.UUID


class HistoryTurn(StrictBody):
    role: str = Field(max_length=20)
    content: str = Field(max_length=20000)


class AllowedVersion(StrictBody):
    versionId: uuid.UUID
    documentId: uuid.UUID
    title: str = Field(default="", max_length=300)
    versionNumber: int = Field(ge=1)


class AnswerBody(StrictBody):
    question: str = Field(min_length=1, max_length=8000)
    history: List[HistoryTurn] = Field(default_factory=list, max_length=20)
    allowedVersions: List[AllowedVersion] = Field(default_factory=list, max_length=2000)
    topK: int = Field(default=6, ge=1, le=50)
    maxContextTokens: int = Field(default=3000, ge=100, le=100_000)
    maxOutputTokens: int = Field(default=700, ge=16, le=8000)
    # Admin configuration (all optional: an absent field uses the environment default). An explicit
    # "temperature": null means "send no temperature" (models without temperature support).
    configVersion: Optional[int] = Field(default=None, ge=0)
    generationModel: Optional[str] = Field(default=None, pattern=r"^[A-Za-z0-9._:-]{1,100}$")
    temperature: Optional[float] = Field(default=None, ge=0, le=1)
    tokenParameter: Optional[Literal["max_tokens", "max_completion_tokens"]] = None
    minScore: Optional[float] = Field(default=None, ge=0, le=1)
    promptVersion: Optional[str] = Field(default=None, min_length=1, max_length=20)

    def generation(self, settings: Settings) -> GenerationOptions:
        temperature = self.temperature if "temperature" in self.model_fields_set else settings.llm_temperature
        return GenerationOptions(self.generationModel or settings.llm_model, temperature, self.tokenParameter or "max_tokens")


@dataclass
class Services:
    settings: Settings
    verifier: SignatureVerifier
    storage: ObjectStorage
    embedder: Embedder
    store: VectorStore
    model: ChatModel
    generations: threading.BoundedSemaphore
    ingestions: threading.BoundedSemaphore


def build_services(settings: Settings, storage: Optional[ObjectStorage] = None, embedder: Optional[Embedder] = None,
                   store: Optional[VectorStore] = None, model: Optional[ChatModel] = None) -> Services:
    settings.validate()
    return Services(
        settings=settings,
        verifier=SignatureVerifier(settings.shared_secret, settings.signature_window_seconds),
        storage=storage or create_storage(settings),
        embedder=embedder or create_embedder(settings),
        store=store or create_vector_store(settings.vector_store, settings.database_url, settings.embedding_dimension),
        model=model or create_chat_model(settings),
        generations=threading.BoundedSemaphore(max(1, settings.max_concurrent_generations)),
        ingestions=threading.BoundedSemaphore(max(1, settings.max_concurrent_ingestions)),
    )


def create_app(services: Optional[Services] = None) -> FastAPI:
    app = FastAPI(title="SmartSolar RAG service", docs_url=None, redoc_url=None, openapi_url=None)
    state = {"services": services}

    def svc() -> Services:
        if state["services"] is None:
            state["services"] = build_services(Settings.from_env())
        return state["services"]

    if services is None:
        svc()  # fail fast at startup on invalid configuration

    async def authenticated_body(request: Request, model: type):
        s = svc()
        declared = request.headers.get("content-length")
        if declared is not None and (not declared.isdigit() or int(declared) > s.settings.max_request_bytes):
            raise HTTPException(status_code=413, detail="request too large")
        body = b""
        async for part in request.stream():
            body += part
            if len(body) > s.settings.max_request_bytes:
                raise HTTPException(status_code=413, detail="request too large")
        reason = s.verifier.verify(request.headers.get(TIMESTAMP_HEADER), request.headers.get(SIGNATURE_HEADER), body)
        if reason is not None:
            logger.warning("Rejected internal call to %s: %s", request.url.path, reason)
            raise HTTPException(status_code=401, detail="unauthorized")
        try:
            return model.model_validate_json(body)
        except ValidationError as exc:
            raise HTTPException(status_code=422, detail="invalid request") from exc

    def acquire(semaphore: threading.BoundedSemaphore, wait_seconds: int) -> None:
        if not semaphore.acquire(timeout=wait_seconds):
            raise HTTPException(status_code=503, detail="busy")

    @app.get("/health/live")
    async def live() -> dict:
        return {"status": "ok"}

    @app.get("/health/ready")
    async def ready() -> JSONResponse:
        ok = await anyio.to_thread.run_sync(svc().store.ready)
        return JSONResponse({"status": "ready" if ok else "not_ready"}, status_code=200 if ok else 503)

    def reject(reason: str) -> None:
        logger.warning("Rejected internal request: %s.", reason)
        raise HTTPException(status_code=422, detail="invalid request")

    @app.post("/internal/v1/ingest")
    async def ingest_endpoint(request: Request) -> dict:
        body: IngestBody = await authenticated_body(request, IngestBody)
        s = svc()
        size = s.settings.chunk_characters if body.chunkCharacters is None else body.chunkCharacters
        overlap = s.settings.chunk_overlap if body.chunkOverlap is None else body.chunkOverlap
        if overlap >= size:
            reject("chunkOverlap must be smaller than chunkCharacters")

        def run() -> dict:
            acquire(s.ingestions, s.settings.queue_wait_seconds)
            try:
                outcome = ingest(s.settings, s.storage, s.embedder, s.store, body.model_dump(mode="json"))
            finally:
                s.ingestions.release()
            logger.info("Ingested version %s: %s (%s chunks).", body.versionId, outcome.status, outcome.chunk_count)
            return {
                "status": outcome.status, "pageCount": outcome.page_count, "chunkCount": outcome.chunk_count, "characters": outcome.characters,
                "embeddingModel": outcome.embedding_model, "embeddingDimension": outcome.embedding_dimension, "error": outcome.error,
                "configVersion": outcome.config_version, "chunkCharacters": outcome.chunk_characters, "chunkOverlap": outcome.chunk_overlap,
            }

        return await anyio.to_thread.run_sync(run)

    @app.post("/internal/v1/delete")
    async def delete_endpoint(request: Request) -> dict:
        body: DeleteBody = await authenticated_body(request, DeleteBody)
        s = svc()
        try:
            deleted = await anyio.to_thread.run_sync(s.store.delete_version, str(body.versionId))
        except Exception as exc:  # noqa: BLE001
            logger.warning("Vector deletion for %s failed (%s).", body.versionId, type(exc).__name__)
            raise HTTPException(status_code=503, detail="vector store unavailable") from exc
        return {"deleted": deleted}

    @app.post("/internal/v1/answer")
    async def answer_endpoint(request: Request) -> dict:
        body: AnswerBody = await authenticated_body(request, AnswerBody)
        s = svc()
        generation = body.generation(s.settings)
        prompt_version = body.promptVersion or s.settings.prompt_version
        if prompt_version not in PROMPT_TEMPLATES:
            reject("unknown prompt version")
        if body.generationModel is not None and s.settings.llm_allowed_models and body.generationModel not in s.settings.llm_allowed_models:
            reject("generation model not allowed")
        min_score = s.settings.min_score if body.minScore is None else body.minScore

        def run() -> dict:
            acquire(s.generations, s.settings.queue_wait_seconds)
            try:
                outcome = answer_question(
                    s.settings, s.embedder, s.store, s.model, body.question,
                    [t.model_dump() for t in body.history], [v.model_dump(mode="json") for v in body.allowedVersions],
                    body.topK, body.maxContextTokens, body.maxOutputTokens,
                    min_score=min_score, prompt_version=prompt_version, generation=generation,
                )
            except ProviderError as exc:
                logger.warning("Answer generation failed: %s.", exc)
                raise HTTPException(status_code=503, detail="provider unavailable") from exc
            except EmbeddingDimensionError as exc:
                logger.error("Embedding configuration does not match the vector schema: %s.", exc)
                raise HTTPException(status_code=503, detail="embedding configuration mismatch") from exc
            finally:
                s.generations.release()
            # Question text is not logged (it may contain personal data).
            logger.info("Answered (grounded=%s, citations=%d, retrieval=%dms, generation=%dms).",
                        outcome.grounded, len(outcome.citations), outcome.retrieval_ms, outcome.generation_ms)
            return {
                "answer": outcome.answer, "grounded": outcome.grounded, "citations": outcome.citations,
                "usage": {"promptTokens": outcome.prompt_tokens, "completionTokens": outcome.completion_tokens},
                "model": outcome.model, "retrievalMs": outcome.retrieval_ms, "generationMs": outcome.generation_ms,
                # What this answer was produced with (after the environment ceilings).
                "configVersion": body.configVersion, "promptVersion": prompt_version,
                "parameters": {
                    "generationModel": generation.model, "temperature": generation.temperature, "tokenParameter": generation.token_parameter,
                    "maxOutputTokens": min(body.maxOutputTokens, s.settings.max_output_tokens),
                    "maxContextTokens": min(body.maxContextTokens, s.settings.max_context_tokens),
                    "topK": min(body.topK, s.settings.max_top_k), "minScore": min_score,
                },
            }

        return await anyio.to_thread.run_sync(run)

    return app


def app_factory() -> FastAPI:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    return create_app()
