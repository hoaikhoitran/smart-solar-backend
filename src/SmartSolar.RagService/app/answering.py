"""Retrieval + grounded generation with citation validation.

Only versions in the caller's allow-list are searched (the API decides what is published). An
answer counts as grounded only when it is not the INSUFFICIENT_CONTEXT sentinel and cites at least
one provided source; citations to non-existent sources are dropped, and only cited sources are
returned to the caller.

Per-request values (top_k, context/output budgets, min score, prompt version and generation options) come from the API's
active configuration; the environment ceilings (RAG_MAX_TOP_K, RAG_MAX_CONTEXT_TOKENS, RAG_MAX_OUTPUT_TOKENS) still clamp them.
"""

from __future__ import annotations

import re
import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional

from .config import Settings
from .embeddings import Embedder, ProviderError, ProviderTimeout, embed_counted
from .llm import ChatModel, GenerationOptions
from .prompting import INSUFFICIENT, build_messages, select_sources
from .vectorstore import VectorStore

_CITATION = re.compile(r"\[(\d{1,3})\]")


@dataclass
class AnswerOutcome:
    answer: Optional[str]
    grounded: bool
    citations: List[dict] = field(default_factory=list)
    prompt_tokens: int = 0
    completion_tokens: int = 0
    model: Optional[str] = None
    retrieval_ms: int = 0
    generation_ms: int = 0
    # The provider's own breakdown of generation_ms (see Completion.timings); None for providers that report none.
    timings: Optional[Dict[str, int]] = None
    # Cost accounting (the API charges billable providers): the question's embedding, and the generation (None = not called:
    # no usable evidence, so no generation was paid for).
    embedding_tokens: int = 0
    embedding_model: Optional[str] = None
    embedding_provider: Optional[str] = None
    generation_provider: Optional[str] = None
    cached_prompt_tokens: int = 0
    reasoning_tokens: int = 0
    usage_reported: bool = True
    context_ms: int = 0
    # Why an answer is not grounded (safe diagnostics, no content): NO_INDEXED_CHUNKS (nothing indexed for the allowed versions with the
    # query embedding model: empty documents or a reindex is required), BELOW_MIN_SCORE (chunks exist but none is relevant enough) or
    # MODEL_INSUFFICIENT (the model found no supported answer or cited nothing provided). None when grounded.
    insufficient_reason: Optional[str] = None
    retrieved_chunks: int = 0
    usable_chunks: int = 0


# Below this much time before the caller's deadline a generation is not started: it could not finish in time.
MIN_GENERATION_SECONDS = 2.0


class DeadlineExceeded(Exception):
    """The caller's deadline passed (or is about to) before the model was called (or the call was cut at it: ``billing`` uncertain)."""

    billing = "none"
    embedding_tokens = 0
    embedding_provider: Optional[str] = None


def _charged(exc: Exception, tokens: int, provider: Optional[str]) -> Exception:
    """Attaches the question's embedding cost to a failure raised after the embedding was paid for."""
    setattr(exc, "embedding_tokens", tokens)
    setattr(exc, "embedding_provider", provider)
    return exc


def answer_question(
    settings: Settings,
    embedder: Embedder,
    store: VectorStore,
    model: ChatModel,
    question: str,
    history: List[Dict[str, str]],
    allowed: List[dict],
    top_k: int,
    max_context_tokens: int,
    max_output_tokens: int,
    min_score: Optional[float] = None,
    prompt_version: str = "v1",
    generation: Optional[GenerationOptions] = None,
    deadline: Optional[float] = None,
    query_embedder: Optional[Embedder] = None,
) -> AnswerOutcome:
    """``deadline`` (``time.monotonic()`` value) is when the caller stops waiting: no generation starts after it, and the model call
    is cut at it, so an answer nobody will read never holds the generation slot (DeadlineExceeded)."""
    threshold = settings.min_score if min_score is None else min_score
    started = time.monotonic()
    titles = {str(v["versionId"]): str(v.get("title") or "") for v in allowed}
    documents = {str(v["versionId"]): str(v["documentId"]) for v in allowed}
    # Questions are embedded with the query embedder (the old model while a reindex to a new model is in progress), and only that
    # model's chunks are searched: models never mix, and a half-reindexed new model is never served.
    query_side = query_embedder or embedder
    provider = getattr(query_side, "provider", None)
    embedded = embed_counted(query_side, [question])
    query, embedding_tokens = embedded.vectors[0], embedded.tokens
    raw = store.search(query, list(titles.keys()), min(top_k, settings.max_top_k), query_side.model)
    # Defence in depth: never trust the store to have applied the filter.
    allowed_hits = [h for h in raw if h.version_id in titles]
    hits = [h for h in allowed_hits if h.score >= threshold]
    retrieval_ms = int((time.monotonic() - started) * 1000)
    usage = dict(embedding_tokens=embedding_tokens, embedding_model=query_side.model, embedding_provider=provider)
    if not hits:
        # No usable evidence: the model is not called (nothing to ground an answer on, nothing paid for generation).
        return AnswerOutcome(None, False, retrieval_ms=retrieval_ms, insufficient_reason="BELOW_MIN_SCORE" if allowed_hits else "NO_INDEXED_CHUNKS",
                             retrieved_chunks=len(allowed_hits), usable_chunks=0, **usage)

    started = time.monotonic()
    sources = select_sources(hits, titles, min(max_context_tokens, settings.max_context_tokens))
    messages = build_messages(question, history, sources, prompt_version=prompt_version)
    context_ms = int((time.monotonic() - started) * 1000)
    started = time.monotonic()
    remaining = None if deadline is None else deadline - time.monotonic()
    if remaining is not None and remaining < MIN_GENERATION_SECONDS:
        raise _charged(DeadlineExceeded(f"{max(0.0, remaining):.1f}s left before the caller's deadline; generation not started"), embedding_tokens, provider)
    try:
        completion = model.complete(messages, min(max_output_tokens, settings.max_output_tokens), generation, timeout_seconds=remaining)
    except ProviderTimeout as exc:
        if deadline is None:
            raise _charged(exc, embedding_tokens, provider)
        # Cut at the caller's deadline: a timeout for the caller, not a provider outage. The provider may have billed it.
        cut = DeadlineExceeded("the model did not finish before the caller's deadline")
        cut.billing = "uncertain"
        raise _charged(cut, embedding_tokens, provider) from exc
    except ProviderError as exc:
        raise _charged(exc, embedding_tokens, provider)
    generation_ms = int((time.monotonic() - started) * 1000)

    text = completion.text.strip()
    by_number = {s.number: s for s in sources}
    cited = []
    for match in _CITATION.finditer(text):
        number = int(match.group(1))
        if number in by_number and number not in cited:
            cited.append(number)
    grounded = bool(text) and INSUFFICIENT not in text and bool(cited)
    if grounded:
        # Remove markers that point at nothing we provided.
        text = _CITATION.sub(lambda m: m.group(0) if int(m.group(1)) in by_number else "", text).strip()
    citations = [
        {
            "documentId": documents[by_number[n].hit.version_id],
            "versionId": by_number[n].hit.version_id,
            "chunkId": by_number[n].hit.chunk_id,
            "page": by_number[n].hit.page,
            "section": by_number[n].hit.section,
            "score": round(by_number[n].hit.score, 4),
            "snippet": by_number[n].hit.content[:300],
        }
        for n in cited
    ] if grounded else []
    return AnswerOutcome(
        answer=text if grounded else None,
        grounded=grounded,
        citations=citations,
        prompt_tokens=completion.prompt_tokens,
        completion_tokens=completion.completion_tokens,
        model=completion.model,
        retrieval_ms=retrieval_ms,
        generation_ms=generation_ms,
        timings=completion.timings,
        generation_provider=getattr(completion, "provider", None) or getattr(model, "provider", None),
        cached_prompt_tokens=getattr(completion, "cached_prompt_tokens", 0),
        reasoning_tokens=getattr(completion, "reasoning_tokens", 0),
        usage_reported=getattr(completion, "usage_reported", True),
        context_ms=context_ms,
        insufficient_reason=None if grounded else "MODEL_INSUFFICIENT",
        retrieved_chunks=len(allowed_hits),
        usable_chunks=len(hits),
        **usage,
    )
