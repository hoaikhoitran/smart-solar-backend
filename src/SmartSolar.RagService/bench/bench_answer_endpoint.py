"""Reproducible end-to-end benchmark of the RAG answer endpoint (``POST /internal/v1/answer``) as the .NET API calls it: signed request,
the API's client timeout, the API's token budgets, optional concurrency. Measures what the user waits for (wall time per request),
the service's own breakdown (retrieval, generation, Ollama load / prompt evaluation / generation), and the outcome mix (200, 503 busy,
503 deadline exceeded, client timeout). Nothing is written except the JSON report on stdout.

Run against a running service (local, Docker or the VPS through an SSH tunnel). Settings come from the environment; the shared secret is
read from RAG_SHARED_SECRET and never printed:

    BENCH_URL=http://127.0.0.1:8000          the RAG service
    BENCH_ALLOWED='[{"versionId": "...", "documentId": "...", "title": "...", "versionNumber": 1}]'   versions the questions may use
    BENCH_REQUESTS=50                        total requests
    BENCH_CONCURRENCY=1                      simultaneous users (each sends its share sequentially)
    BENCH_CLIENT_TIMEOUT=59                  what the API waits (Rag:RequestTimeoutSeconds 60, minus 1 s)
    BENCH_SEND_DEADLINE=1                    send deadlineSeconds like the fixed API (0 = behave like the previous API)
    BENCH_MAX_CONTEXT_TOKENS=3000            the API's Rag:MaxContextTokens
    BENCH_MAX_OUTPUT_TOKENS=700              the API's Rag:MaxOutputTokens
    BENCH_TOP_K=6
    BENCH_LABEL=...                          free text copied into the report (hardware, model, cold/warm)

The questions are a fixed latency set (not an answer-quality dataset): they exercise retrieval and generation with realistic lengths.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import statistics
import threading
import time
from typing import Any, Dict, List

import httpx

QUESTIONS = [
    "Tấm pin mặt trời được bảo hành bao lâu?",
    "Inverter được bảo hành mấy năm và khi hỏng tôi phải làm gì?",
    "Bảo hành hiệu suất của tấm pin là bao nhiêu năm?",
    "Kỹ thuật viên đến kiểm tra trong bao lâu sau khi tôi báo lỗi?",
    "Hệ thống 5 kWp sản xuất khoảng bao nhiêu điện mỗi tháng?",
    "Gói bảo trì định kỳ gồm những gì?",
    "Thiên tai làm hỏng hệ thống thì có được hỗ trợ không?",
    "Tôi cần chuẩn bị gì trước khi lắp đặt?",
    "Bảo hành có áp dụng khi tôi tự di dời tấm pin không?",
    "Làm sao để báo lỗi thiết bị qua cổng khách hàng?",
]


def env_int(name: str, default: int) -> int:
    return int(os.environ.get(name, default))


def percentile(values: List[float], p: float) -> float:
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round(p / 100 * (len(ordered) - 1))))
    return round(ordered[index], 2)


def summary(values: List[float]) -> Dict[str, Any]:
    if not values:
        return {"n": 0}
    return {"n": len(values), "p50": percentile(values, 50), "p95": percentile(values, 95), "p99": percentile(values, 99),
            "max": round(max(values), 2), "mean": round(statistics.mean(values), 2)}


def main() -> None:
    url = os.environ.get("BENCH_URL", "http://127.0.0.1:8000").rstrip("/")
    secret = os.environ["RAG_SHARED_SECRET"]
    allowed = json.loads(os.environ["BENCH_ALLOWED"])
    total = env_int("BENCH_REQUESTS", 50)
    concurrency = max(1, env_int("BENCH_CONCURRENCY", 1))
    client_timeout = float(os.environ.get("BENCH_CLIENT_TIMEOUT", "59"))
    send_deadline = os.environ.get("BENCH_SEND_DEADLINE", "1") == "1"
    payload_base = {"history": [], "allowedVersions": allowed, "topK": env_int("BENCH_TOP_K", 6),
                    "maxContextTokens": env_int("BENCH_MAX_CONTEXT_TOKENS", 3000), "maxOutputTokens": env_int("BENCH_MAX_OUTPUT_TOKENS", 700)}
    if send_deadline:
        payload_base["deadlineSeconds"] = max(1, client_timeout - 1)

    results: List[Dict[str, Any]] = []
    lock = threading.Lock()
    counter = {"next": 0}

    def worker() -> None:
        with httpx.Client(timeout=client_timeout) as client:
            while True:
                with lock:
                    index = counter["next"]
                    if index >= total:
                        return
                    counter["next"] += 1
                body = json.dumps({**payload_base, "question": QUESTIONS[index % len(QUESTIONS)]}).encode()
                ts = str(int(time.time()))
                signature = hmac.new(secret.encode(), ts.encode() + b"." + body, hashlib.sha256).hexdigest()
                started = time.monotonic()
                record: Dict[str, Any] = {"index": index}
                try:
                    response = client.post(f"{url}/internal/v1/answer", content=body, headers={
                        "Content-Type": "application/json", "X-SmartSolar-Timestamp": ts, "X-SmartSolar-Signature": signature})
                    record["status"] = response.status_code
                    data = response.json() if response.headers.get("content-type", "").startswith("application/json") else {}
                    if response.status_code == 200:
                        record.update(grounded=data.get("grounded"), citations=len(data.get("citations") or []),
                                      retrievalMs=data.get("retrievalMs"), generationMs=data.get("generationMs"), timings=data.get("timings"),
                                      promptTokens=(data.get("usage") or {}).get("promptTokens"),
                                      completionTokens=(data.get("usage") or {}).get("completionTokens"))
                    else:
                        record["detail"] = data.get("detail")
                except httpx.TimeoutException:
                    record["status"] = "client-timeout"
                except httpx.HTTPError as exc:
                    record["status"] = f"error:{type(exc).__name__}"
                record["seconds"] = round(time.monotonic() - started, 3)
                with lock:
                    results.append(record)

    started = time.monotonic()
    threads = [threading.Thread(target=worker) for _ in range(concurrency)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    wall = time.monotonic() - started

    ok = [r for r in results if r["status"] == 200]
    outcomes: Dict[str, int] = {}
    for r in results:
        key = str(r["status"]) + (f" {r['detail']}" if r.get("detail") else "")
        outcomes[key] = outcomes.get(key, 0) + 1

    def timing(name: str) -> List[float]:
        return [r["timings"][name] / 1000 for r in ok if isinstance(r.get("timings"), dict) and isinstance(r["timings"].get(name), (int, float))]

    report = {
        "label": os.environ.get("BENCH_LABEL", ""),
        "settings": {"requests": total, "concurrency": concurrency, "clientTimeoutSeconds": client_timeout, "sendDeadline": send_deadline,
                     "maxContextTokens": payload_base["maxContextTokens"], "maxOutputTokens": payload_base["maxOutputTokens"],
                     "topK": payload_base["topK"]},
        "wallSeconds": round(wall, 1),
        "outcomes": outcomes,
        "successRate": round(len(ok) / max(1, len(results)), 3),
        "endToEndSeconds": {"all": summary([r["seconds"] for r in results]), "success": summary([r["seconds"] for r in ok])},
        "serviceSeconds": {"retrieval": summary([r["retrievalMs"] / 1000 for r in ok if r.get("retrievalMs") is not None]),
                           "generation": summary([r["generationMs"] / 1000 for r in ok if r.get("generationMs") is not None]),
                           "ollamaLoad": summary(timing("loadMs")), "ollamaPromptEval": summary(timing("promptEvalMs")),
                           "ollamaEval": summary(timing("evalMs"))},
        "tokens": {"prompt": summary([float(r["promptTokens"]) for r in ok if r.get("promptTokens")]),
                   "completion": summary([float(r["completionTokens"]) for r in ok if r.get("completionTokens")])},
        "grounded": sum(1 for r in ok if r.get("grounded")),
        "requests": sorted(results, key=lambda r: r["index"]),
    }
    print(json.dumps(report, ensure_ascii=False, indent=1))


if __name__ == "__main__":
    main()
