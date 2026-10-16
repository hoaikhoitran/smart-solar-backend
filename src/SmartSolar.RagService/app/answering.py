"""Retrieval + grounded generation with citation validation.

Only versions in the caller's allow-list are searched (the API decides what is published). An
answer counts as grounded only when it is not the INSUFFICIENT_CONTEXT sentinel and cites at least
one provided source; citations to non-existent sources are dropped, and only cited sources are
returned to the caller.
"""

from __future__ import annotations

import re
import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional

from .config import Settings
from .embeddings import Embedder
from .llm import ChatModel
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
) -> AnswerOutcome:
    started = time.monotonic()
    titles = {str(v["versionId"]): str(v.get("title") or "") for v in allowed}
    documents = {str(v["versionId"]): str(v["documentId"]) for v in allowed}
    query = embedder.embed([question])[0]
    hits = store.search(query, list(titles.keys()), min(top_k, settings.max_top_k), embedder.model)
    # Defence in depth: never trust the store to have applied the filter.
    hits = [h for h in hits if h.version_id in titles and h.score >= settings.min_score]
    retrieval_ms = int((time.monotonic() - started) * 1000)
    if not hits:
        return AnswerOutcome(None, False, retrieval_ms=retrieval_ms)

    sources = select_sources(hits, titles, min(max_context_tokens, settings.max_context_tokens))
    messages = build_messages(question, history, sources)
    started = time.monotonic()
    completion = model.complete(messages, min(max_output_tokens, settings.max_output_tokens))
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
    )
