"""Reproducible latency benchmark of the local (Ollama) RAG providers, using the service's own adapters and prompt builder.

Run it where the RAG service runs, with the service's environment (RAG_*_PROVIDER=ollama, RAG_*_BASE_URL, models):

    docker compose -f docker-compose.pull.yml --profile rag exec -T -e BENCH_RUNS=10 rag-service python - < bench/bench_local_rag.py

and sample memory/CPU of every container at the same time (separate shell):

    while true; do docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}'; sleep 2; done | tee stats.log

It prints one JSON document. Nothing is written to the database or S3; the models are unloaded first to measure a cold start
(set BENCH_SKIP_COLD=1 on a live system to keep them loaded). Settings (environment):

    BENCH_RUNS=10              warm generations (sequential), for p50/p95
    BENCH_CONCURRENCY=2        simultaneous generations in the concurrency step (0 = skip)
    BENCH_CONTEXT_TOKENS       retrieved context per question (default: RAG_MAX_CONTEXT_TOKENS, capped like a real request at 3000)
    BENCH_OUTPUT_TOKENS        answer budget (default 700, the API's MaxOutputTokens)
    BENCH_CHUNKS=20            chunks embedded in the ingestion step (RAG_CHUNK_CHARACTERS each)
    BENCH_OLLAMA_OPTIONS       extra JSON merged into every Ollama "options" (e.g. {"num_thread":3}); empty on the VPS
    BENCH_PROMPT_VERSION       system prompt version (default RAG_PROMPT_VERSION): run once with v1 and once with v2 to compare
    BENCH_TARGET_P95_SECONDS=10  the NFR the warm p95 is compared with
"""

from __future__ import annotations

import json
import os
import statistics
import sys
import threading
import time
import uuid
from typing import Any, Dict, List

import httpx

sys.path.insert(0, os.getcwd())

from app.config import Settings  # noqa: E402
from app.embeddings import OllamaEmbedder  # noqa: E402
from app.llm import OllamaChatModel, estimate_tokens  # noqa: E402
from app.prompting import NumberedSource, build_messages  # noqa: E402
from app.vectorstore import SearchHit  # noqa: E402

PARAGRAPH = ("Tấm pin mặt trời của SmartSolar được bảo hành sản phẩm 12 năm và bảo hành hiệu suất 25 năm kể từ ngày bàn giao. "
             "Inverter được bảo hành 5 năm; khách hàng báo lỗi qua cổng khách hàng và kỹ thuật viên đến kiểm tra trong 3 ngày làm việc. "
             "Hệ thống 5 kWp áp mái sản xuất trung bình 600 kWh mỗi tháng tùy hướng mái và thời tiết. ")
QUESTION = "Tấm pin và inverter được bảo hành bao lâu, và khi hỏng thì tôi phải làm gì?"
# Per process: an earlier benchmark run against the same server must not leave cached prompt prefixes for this one.
NONCE = uuid.uuid4().hex[:8]


class OptionsTransport(httpx.BaseTransport):
    """Adds BENCH_OLLAMA_OPTIONS to the JSON body of every Ollama call (benchmark only; the service never does this)."""

    def __init__(self, extra: Dict[str, Any]) -> None:
        self._inner = httpx.HTTPTransport()
        self._extra = extra

    def handle_request(self, request: httpx.Request) -> httpx.Response:
        if self._extra and request.content:
            body = json.loads(request.content)
            body["options"] = {**(body.get("options") or {}), **self._extra}
            content = json.dumps(body).encode()
            request = httpx.Request(request.method, request.url, headers={**request.headers, "content-length": str(len(content))}, content=content)
        return self._inner.handle_request(request)


def percentile(values: List[float], p: float) -> float:
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round(p / 100 * (len(ordered) - 1))))
    return round(ordered[index], 2)


def summary(values: List[float]) -> Dict[str, float]:
    return {"n": len(values), "p50": percentile(values, 50), "p95": percentile(values, 95), "max": round(max(values), 2),
            "mean": round(statistics.mean(values), 2)} if values else {"n": 0}


def sources(context_tokens: int, chunk_characters: int, run: int = 0) -> List[NumberedSource]:
    # Every run gets different source text: real questions retrieve different chunks, so Ollama's prompt cache must not make a
    # repeated prompt look fast (only the shared system prompt may be cached, as in production).
    text = (f"[Tài liệu {NONCE}-{run}] " + PARAGRAPH * (chunk_characters // len(PARAGRAPH) + 1))[:chunk_characters]
    chosen: List[NumberedSource] = []
    used = 0
    while not chosen or used + estimate_tokens(text) + 30 <= context_tokens:
        n = len(chosen) + 1
        chosen.append(NumberedSource(n, SearchHit(f"c{n}", "v", "d", n, None, text, 0.8), "Chính sách bảo hành"))
        used += estimate_tokens(text) + 30
    return chosen


def main() -> None:
    settings = Settings.from_env()
    if settings.embedding_provider != "ollama" or settings.llm_provider != "ollama":
        sys.exit("RAG_EMBEDDING_PROVIDER and RAG_LLM_PROVIDER must both be ollama")
    settings.provider_timeout_seconds = max(settings.provider_timeout_seconds, 600)
    extra = json.loads(os.environ.get("BENCH_OLLAMA_OPTIONS") or "{}")
    transport = OptionsTransport(extra)
    embedder, model = OllamaEmbedder(settings, transport), OllamaChatModel(settings, transport)
    runs, concurrency = int(os.environ.get("BENCH_RUNS", "10")), int(os.environ.get("BENCH_CONCURRENCY", "2"))
    context_tokens = int(os.environ.get("BENCH_CONTEXT_TOKENS") or min(settings.max_context_tokens, 3000))
    output_tokens = int(os.environ.get("BENCH_OUTPUT_TOKENS", "700"))
    target = float(os.environ.get("BENCH_TARGET_P95_SECONDS", "10"))
    prompt_version = os.environ.get("BENCH_PROMPT_VERSION") or settings.prompt_version
    raw = httpx.Client(base_url=settings.llm_base_url.rstrip("/") + "/", timeout=600)
    messages = build_messages(QUESTION, [], sources(context_tokens, settings.chunk_characters), prompt_version=prompt_version)
    fresh = iter(range(1, 10_000))

    def question_messages() -> List[Dict[str, str]]:
        return build_messages(QUESTION, [], sources(context_tokens, settings.chunk_characters, next(fresh)), prompt_version=prompt_version)
    report: Dict[str, Any] = {"embeddingModel": settings.embedding_model, "generationModel": settings.llm_model, "numCtx": settings.llm_context_tokens,
                              "promptVersion": prompt_version, "systemPromptTokensEstimated": estimate_tokens(messages[0]["content"]),
                              "contextTokensRequested": context_tokens, "promptTokensEstimated": sum(estimate_tokens(m["content"]) for m in messages),
                              "outputTokens": output_tokens, "extraOptions": extra}

    if os.environ.get("BENCH_SKIP_COLD") != "1":
        for name in (settings.llm_model, settings.embedding_model):
            raw.post("api/generate", json={"model": name, "keep_alive": 0})
        time.sleep(2)
        started = time.monotonic()
        dimension = len(embedder.embed(["khởi động"])[0])
        report["coldEmbeddingSeconds"] = round(time.monotonic() - started, 2)
        report["embeddingDimension"] = dimension
        started = time.monotonic()
        model.complete([{"role": "user", "content": "Xin chào"}], 8)
        report["coldGenerationLoadSeconds"] = round(time.monotonic() - started, 2)

    chunk = (PARAGRAPH * (settings.chunk_characters // len(PARAGRAPH) + 1))[:settings.chunk_characters]
    chunk_count = int(os.environ.get("BENCH_CHUNKS", "20"))
    started = time.monotonic()
    embedder.embed([f"{i}. {chunk}" for i in range(chunk_count)])
    elapsed = time.monotonic() - started
    report["ingestion"] = {"chunks": chunk_count, "charactersPerChunk": settings.chunk_characters, "seconds": round(elapsed, 2),
                           "secondsPerChunk": round(elapsed / chunk_count, 3)}
    started = time.monotonic()
    for _ in range(5):
        embedder.embed([QUESTION])
    report["queryEmbeddingSeconds"] = round((time.monotonic() - started) / 5, 3)

    latencies: List[float] = []
    completions: List[int] = []
    timings: List[Dict[str, int]] = []
    for _ in range(runs):
        prompt = question_messages()
        started = time.monotonic()
        completion = model.complete(prompt, output_tokens)
        latencies.append(time.monotonic() - started)
        completions.append(completion.completion_tokens)
        if completion.timings:
            timings.append(completion.timings)
    report["warmGeneration"] = {**summary(latencies), "completionTokensMean": round(statistics.mean(completions), 1) if completions else 0,
                                "promptTokensReported": completion.prompt_tokens if runs else None,
                                "sampleAnswer": completion.text[:600] if runs else None}
    if timings:
        # Ollama's own breakdown: prompt evaluation (the retrieved context) against generation, and the generation rate.
        eval_ms = [t.get("evalMs", 0) for t in timings]
        report["warmGeneration"]["ollama"] = {
            "loadMsMedian": statistics.median(t.get("loadMs", 0) for t in timings),
            "promptEvalMsMedian": statistics.median(t.get("promptEvalMs", 0) for t in timings),
            "evalMsMedian": statistics.median(eval_ms),
            "tokensPerSecond": round(statistics.mean(c * 1000 / e for c, e in zip(completions, eval_ms) if e > 0), 1) if any(eval_ms) else None,
        }
    report["warmGeneration"]["nfrP95Seconds"] = target
    report["warmGeneration"]["nfr"] = "PASS" if runs and percentile(latencies, 95) <= target else "FAIL"

    if concurrency > 0:
        results: List[float] = []
        errors: List[str] = []

        prompts = [question_messages() for _ in range(concurrency)]

        def one(prompt: List[Dict[str, str]]) -> None:
            began = time.monotonic()
            try:
                model.complete(prompt, output_tokens)
                results.append(time.monotonic() - began)
            except Exception as exc:  # noqa: BLE001 - reported, not raised
                errors.append(type(exc).__name__)

        threads = [threading.Thread(target=one, args=(p,)) for p in prompts]
        started = time.monotonic()
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        report["concurrent"] = {"requests": concurrency, "wallSeconds": round(time.monotonic() - started, 2), "latency": summary(results), "errors": errors}

    loaded = raw.get("api/ps").json().get("models", [])
    report["loadedModels"] = [{"name": m.get("name"), "sizeMB": round(m.get("size", 0) / 1e6), "vramMB": round(m.get("size_vram", 0) / 1e6),
                               "contextLength": m.get("context_length")} for m in loaded]
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
