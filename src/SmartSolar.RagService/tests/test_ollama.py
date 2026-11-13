"""Local inference through Ollama: configuration, the native /api/embed and /api/chat contracts, and the end-to-end flow.

Every Ollama call here goes to an httpx.MockTransport that answers in Ollama's native format (verified against a real
Ollama server, see guides/rag-local-ollama.md). The live smoke test at the end is opt-in (RAG_LIVE_OLLAMA_URL).
"""

import json
import math
import os
import uuid

import httpx
import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.embeddings import EmbeddingContractError, HashingEmbedder, OllamaEmbedder, ProviderError, create_embedder
from app.ingestion import FAILED, READY, ingest
from app.llm import ExtractiveChatModel, OllamaChatModel, create_chat_model
from app.main import build_services, create_app
from app.storage import LocalStorage
from app.vectorstore import InMemoryVectorStore
from tests.conftest import SECRET, sha
from tests.test_runtime_config import ingest_request, post, put

EMBED_MODEL = "qwen3-embedding:0.6b"
CHAT_MODEL = "qwen3:1.7b"
BASE = "http://ollama:11434"
WARRANTY = ("Tấm pin mặt trời được bảo hành sản phẩm 12 năm và bảo hành hiệu suất 25 năm. " * 6 + "\n\n") * 3


def local_settings(**overrides):
    values = dict(shared_secret=SECRET, storage="local", embedding_provider="ollama", embedding_model=EMBED_MODEL, embedding_dimension=1024,
                  embedding_base_url=BASE, llm_provider="ollama", llm_model=CHAT_MODEL, llm_base_url=BASE, min_score=0.05,
                  chunk_characters=400, chunk_overlap=60)
    values.update(overrides)
    return Settings(**values)


def production(**overrides):
    return local_settings(environment="production", vector_store="pgvector", database_url="postgresql://rag@db/smartsolar",
                          storage="s3", s3_allowed_buckets=["kb"], **overrides)


class FakeOllama:
    """Answers /api/embed and /api/chat like Ollama does; a test can break the contract on purpose."""

    def __init__(self, dimension=1024, embed_model=EMBED_MODEL, status=200, raise_exc=None, embed_body=None, chat_body=None,
                 count_delta=0, poison=None, thinking=""):
        self.requests = []
        self.dimension, self.embed_model, self.status, self.raise_exc = dimension, embed_model, status, raise_exc
        self.embed_body, self.chat_body, self.count_delta, self.poison, self.thinking = embed_body, chat_body, count_delta, poison, thinking
        self._vectors = HashingEmbedder(dimension, "fake")
        self._answers = ExtractiveChatModel(CHAT_MODEL)
        self.pulled = [EMBED_MODEL, CHAT_MODEL]

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        if self.raise_exc is not None:
            raise self.raise_exc
        if self.status != 200:
            return httpx.Response(self.status, json={"error": f"model '{CHAT_MODEL}' not found"})
        if request.url.path == "/api/tags":
            return httpx.Response(200, json={"models": [{"name": m, "model": m, "size": 1} for m in self.pulled]})
        payload = json.loads(request.content)
        if request.url.path == "/api/embed":
            if self.embed_body is not None:
                return httpx.Response(200, content=self.embed_body)
            vectors = self._vectors.embed(payload["input"])
            if self.poison is not None:
                vectors[0][0] = self.poison
            body = {"model": self.embed_model, "embeddings": vectors[: len(vectors) + self.count_delta], "total_duration": 1, "prompt_eval_count": 3}
            return httpx.Response(200, content=json.dumps(body).encode(), headers={"content-type": "application/json"})
        if request.url.path == "/api/chat":
            if self.chat_body is not None:
                return httpx.Response(200, content=self.chat_body)
            text = self._answers.complete(payload["messages"], 100).text
            body = {"model": payload["model"], "created_at": "2026-11-12T00:00:00Z", "message": {"role": "assistant", "content": self.thinking + text},
                    "done": True, "done_reason": "stop", "prompt_eval_count": 321, "eval_count": 12}
            return httpx.Response(200, json=body)
        return httpx.Response(404, json={"error": "not found"})


def embedder(fake, **overrides):
    return OllamaEmbedder(local_settings(**overrides), transport=httpx.MockTransport(fake))


def chat(fake, **overrides):
    return OllamaChatModel(local_settings(**overrides), transport=httpx.MockTransport(fake))


# ------------------------------------------------------------------ configuration

def test_local_providers_need_no_api_key_and_are_accepted_in_production():
    local_settings().validate()
    production().validate()
    assert isinstance(create_embedder(local_settings()), OllamaEmbedder)
    assert isinstance(create_chat_model(local_settings()), OllamaChatModel)


def test_production_still_refuses_the_development_stand_ins():
    with pytest.raises(ValueError, match="production requires real embedding and LLM providers"):
        production(llm_provider="extractive", llm_model="extractive-dev").validate()
    with pytest.raises(ValueError, match="only allowed with RAG_ENV=development or test"):
        production(embedding_provider="hashing", embedding_model="hashing-dev").validate()


@pytest.mark.parametrize("overrides,message", [
    (dict(embedding_base_url=None), "RAG_EMBEDDING_BASE_URL is required for ollama embeddings"),
    (dict(llm_base_url=None), "RAG_LLM_BASE_URL is required for the ollama LLM provider"),
    (dict(embedding_dimension=1536), "RAG_EMBEDDING_DIMENSION must be 1024 for RAG_EMBEDDING_MODEL=qwen3-embedding:0.6b"),
    (dict(embedding_model="text-embedding-3-small"), "must name an Ollama embedding model"),
    (dict(llm_model="extractive-dev"), "RAG_LLM_MODEL must name an Ollama model"),
    (dict(llm_context_tokens=2048), "RAG_LLM_CONTEXT_TOKENS must be at least"),
    (dict(llm_provider="local"), "RAG_LLM_PROVIDER must be openai, ollama or extractive"),
])
def test_invalid_local_configuration_is_refused(overrides, message):
    with pytest.raises(ValueError, match=message):
        local_settings(**overrides).validate()


def test_pgvector_accepts_only_dimensions_with_an_approved_schema():
    production().validate()  # 1024: sql/knowledge_chunk_local_1024.sql
    with pytest.raises(ValueError, match="must be 1536 or 1024 to match knowledge_chunk.embedding"):
        production(embedding_model="nomic-embed-text", embedding_dimension=768).validate()


def test_environment_variables_configure_the_local_providers(monkeypatch):
    for key, value in {"RAG_EMBEDDING_PROVIDER": "ollama", "RAG_EMBEDDING_MODEL": EMBED_MODEL, "RAG_EMBEDDING_DIMENSION": "1024",
                       "RAG_EMBEDDING_BASE_URL": BASE, "RAG_LLM_PROVIDER": "ollama", "RAG_LLM_MODEL": CHAT_MODEL, "RAG_LLM_BASE_URL": BASE,
                       "RAG_LLM_CONTEXT_TOKENS": "9000", "RAG_OLLAMA_KEEP_ALIVE": "1h", "RAG_SHARED_SECRET": SECRET, "RAG_STORAGE": "local"}.items():
        monkeypatch.setenv(key, value)
    monkeypatch.delenv("RAG_EMBEDDING_API_KEY", raising=False)
    monkeypatch.delenv("RAG_LLM_API_KEY", raising=False)
    settings = Settings.from_env()
    settings.validate()
    assert (settings.embedding_model, settings.embedding_dimension, settings.embedding_api_key) == (EMBED_MODEL, 1024, None)
    assert (settings.llm_model, settings.llm_context_tokens, settings.ollama_keep_alive) == (CHAT_MODEL, 9000, "1h")


# ------------------------------------------------------------------ /api/embed contract

def test_embed_request_follows_the_native_contract_without_credentials():
    fake = FakeOllama()
    vectors = embedder(fake, embedding_batch_size=2).embed(["một", "hai", "ba"])

    assert [len(v) for v in vectors] == [1024, 1024, 1024]
    assert len(fake.requests) == 2  # batched
    request = fake.requests[0]
    assert request.url == httpx.URL(f"{BASE}/api/embed")
    assert "authorization" not in request.headers
    assert json.loads(request.content) == {"model": EMBED_MODEL, "input": ["một", "hai"], "truncate": False, "keep_alive": "30m"}


@pytest.mark.parametrize("fake,error", [
    (FakeOllama(dimension=768), "768 dimensions, expected 1024"),
    (FakeOllama(count_delta=-1), "unexpected number of vectors"),
    (FakeOllama(poison=float("nan")), "non-finite"),
    (FakeOllama(embed_model="nomic-embed-text"), "answered with model"),
    (FakeOllama(embed_body=b"<html>"), "non-JSON"),
    (FakeOllama(embed_body=b"[]"), "unexpected body"),
])
def test_embedding_contract_violations_are_refused(fake, error):
    with pytest.raises(EmbeddingContractError, match=error):
        embedder(fake).embed(["bảo hành"])


@pytest.mark.parametrize("fake,error", [
    (FakeOllama(status=404), "HTTP 404"),
    (FakeOllama(status=500), "HTTP 500"),
    (FakeOllama(raise_exc=httpx.ReadTimeout("slow")), "timed out"),
    (FakeOllama(raise_exc=httpx.ConnectError("down")), "unreachable"),
])
def test_ollama_unavailable_is_a_provider_error(fake, error):
    with pytest.raises(ProviderError, match=error):
        embedder(fake).embed(["bảo hành"])


# ------------------------------------------------------------------ /api/chat contract

def test_chat_request_disables_thinking_and_sets_the_context_window():
    fake = FakeOllama()
    model = chat(fake, llm_context_tokens=6144, llm_temperature=0.2)
    completion = model.complete([{"role": "user", "content": '<source id="1">Bảo hành 12 năm.</source>'}], 300)

    request = fake.requests[0]
    assert request.url == httpx.URL(f"{BASE}/api/chat")
    assert "authorization" not in request.headers
    body = json.loads(request.content)
    assert (body["model"], body["stream"], body["think"], body["keep_alive"]) == (CHAT_MODEL, False, False, "30m")
    assert body["options"] == {"num_predict": 300, "num_ctx": 6144, "temperature": 0.2}
    assert (completion.text, completion.prompt_tokens, completion.completion_tokens) == ("Bảo hành 12 năm. [1]", 321, 12)


def test_chat_payload_omits_a_null_temperature_and_uses_the_requested_model():
    from app.llm import GenerationOptions

    body = chat(FakeOllama()).payload([{"role": "user", "content": "x"}], 64, GenerationOptions("qwen3:0.6b", None, "max_completion_tokens"))
    assert body["model"] == "qwen3:0.6b"
    assert body["options"] == {"num_predict": 64, "num_ctx": 8192}


def test_a_thinking_block_is_never_returned_as_the_answer():
    fake = FakeOllama(thinking="<think>\nreasoning that must not leak [9]\n</think>\n\n")
    completion = chat(fake).complete([{"role": "user", "content": '<source id="1">Bảo hành 12 năm.</source>'}], 100)
    assert completion.text == "Bảo hành 12 năm. [1]"


@pytest.mark.parametrize("fake,error", [
    (FakeOllama(status=404), "HTTP 404"),
    (FakeOllama(raise_exc=httpx.ReadTimeout("slow")), "timed out"),
    (FakeOllama(raise_exc=httpx.ConnectError("down")), "unreachable"),
    (FakeOllama(chat_body=b"not json"), "non-JSON"),
    (FakeOllama(chat_body=json.dumps({"model": CHAT_MODEL, "done": True}).encode()), "no message content"),
    (FakeOllama(chat_body=json.dumps({"message": {"role": "assistant", "content": None}}).encode()), "no message content"),
])
def test_chat_failures_and_malformed_replies_are_provider_errors(fake, error):
    with pytest.raises(ProviderError, match=error):
        chat(fake).complete([{"role": "user", "content": "x"}], 100)


# ------------------------------------------------------------------ end to end through the API

def local_app(tmp_path, fake):
    settings = local_settings(local_storage_dir=str(tmp_path))
    services = build_services(settings, storage=LocalStorage(str(tmp_path)), embedder=OllamaEmbedder(settings, httpx.MockTransport(fake)),
                              store=InMemoryVectorStore(1024), model=OllamaChatModel(settings, httpx.MockTransport(fake)))
    return settings, services, TestClient(create_app(services))


def allowed(version, document):
    return [{"versionId": version, "documentId": document, "title": "Bảo hành", "versionNumber": 1}]


def test_ingest_store_retrieve_and_answer_with_citations(tmp_path):
    fake = FakeOllama()
    settings, services, client = local_app(tmp_path, fake)
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    data = WARRANTY.encode()
    put(settings, "kb/warranty.txt", data)

    ingested = post(client, "/internal/v1/ingest", ingest_request(version, document, "kb/warranty.txt", data)).json()
    assert (ingested["status"], ingested["embeddingModel"], ingested["embeddingDimension"]) == (READY, EMBED_MODEL, 1024)
    assert services.store.count(version) == ingested["chunkCount"] > 0

    answer = post(client, "/internal/v1/answer", {"question": "Tấm pin bảo hành bao lâu?", "allowedVersions": allowed(version, document)}).json()
    assert answer["grounded"] is True
    assert answer["model"] == CHAT_MODEL
    assert answer["citations"] and all(c["versionId"] == version and c["documentId"] == document for c in answer["citations"])
    assert "[1]" in answer["answer"]
    assert answer["usage"] == {"promptTokens": 321, "completionTokens": 12}


def test_no_matching_source_gives_the_insufficient_answer_without_calling_the_model(tmp_path):
    fake = FakeOllama()
    _, _, client = local_app(tmp_path, fake)
    answer = post(client, "/internal/v1/answer", {"question": "Giá cổ phiếu hôm nay?", "allowedVersions": allowed(str(uuid.uuid4()), str(uuid.uuid4()))}).json()
    assert (answer["grounded"], answer["answer"], answer["citations"]) == (False, None, [])
    assert not [r for r in fake.requests if r.url.path == "/api/chat"]


def test_a_model_answer_without_citations_is_not_grounded(tmp_path):
    fake = FakeOllama()
    settings, _, client = local_app(tmp_path, fake)
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    data = WARRANTY.encode()
    put(settings, "kb/w.txt", data)
    post(client, "/internal/v1/ingest", ingest_request(version, document, "kb/w.txt", data))
    fake.chat_body = json.dumps({"model": CHAT_MODEL, "message": {"role": "assistant", "content": "Chắc khoảng 10 năm."}, "done": True}).encode()

    answer = post(client, "/internal/v1/answer", {"question": "Tấm pin bảo hành bao lâu?", "allowedVersions": allowed(version, document)}).json()
    assert (answer["grounded"], answer["answer"], answer["citations"]) == (False, None, [])


def test_ollama_down_fails_the_answer_with_503_and_the_ingest_as_failed(tmp_path):
    fake = FakeOllama()
    settings, services, client = local_app(tmp_path, fake)
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    data = WARRANTY.encode()
    put(settings, "kb/w.txt", data)
    post(client, "/internal/v1/ingest", ingest_request(version, document, "kb/w.txt", data))
    chunks = services.store.count(version)

    fake.raise_exc = httpx.ConnectError("ollama stopped")
    assert post(client, "/internal/v1/answer", {"question": "Bảo hành?", "allowedVersions": allowed(version, document)}).status_code == 503
    # Called directly: an identical signed call within the same second is (correctly) refused as a replay.
    reindex = ingest(settings, services.storage, services.embedder, services.store, ingest_request(version, document, "kb/w.txt", data))
    assert reindex.status == FAILED
    assert services.store.count(version) == chunks  # a failed reindex keeps the previous chunks


def test_unsigned_calls_are_still_rejected_with_local_providers(tmp_path):
    _, _, client = local_app(tmp_path, FakeOllama())
    assert client.post("/internal/v1/answer", json={"question": "x"}).status_code == 401


def test_a_wrong_dimension_from_ollama_never_reaches_the_store(tmp_path):
    fake = FakeOllama(dimension=768)
    settings, services, client = local_app(tmp_path, fake)
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    data = WARRANTY.encode()
    put(settings, "kb/w.txt", data)
    outcome = ingest(settings, services.storage, services.embedder, services.store, ingest_request(version, document, "kb/w.txt", data))
    assert outcome.status == FAILED
    assert services.store.count(version) == 0


# ------------------------------------------------------------------ opt-in live check against a real Ollama

@pytest.mark.skipif(not os.environ.get("RAG_LIVE_OLLAMA_URL"), reason="set RAG_LIVE_OLLAMA_URL to a local Ollama with the models pulled")
def test_live_ollama_contract():
    url = os.environ["RAG_LIVE_OLLAMA_URL"]
    settings = local_settings(embedding_base_url=url, llm_base_url=url, provider_timeout_seconds=300)
    vectors = OllamaEmbedder(settings).embed(["Tấm pin bảo hành 12 năm.", "Inverter bảo hành 5 năm."])
    assert [len(v) for v in vectors] == [1024, 1024]
    assert all(math.isfinite(x) for v in vectors for x in v)
    completion = OllamaChatModel(settings).complete(
        [{"role": "system", "content": "Trả lời ngắn gọn bằng tiếng Việt, kèm [1]."},
         {"role": "user", "content": '<source id="1">Tấm pin được bảo hành sản phẩm 12 năm.</source>\nTấm pin bảo hành bao lâu?'}], 80)
    assert completion.text and "<think>" not in completion.text
    assert sha(completion.text.encode())  # any non-empty text


# ------------------------------------------------------------------ readiness and CPU threads

def test_ready_only_once_both_local_models_are_pulled(tmp_path):
    fake = FakeOllama()
    fake.pulled = [EMBED_MODEL]
    _, _, client = local_app(tmp_path, fake)
    response = client.get("/health/ready")
    assert (response.status_code, response.json()) == (503, {"status": "not_ready"})
    fake.pulled = [EMBED_MODEL, CHAT_MODEL]
    response = client.get("/health/ready")
    assert (response.status_code, response.json()) == (200, {"status": "ready"})


@pytest.mark.parametrize("pulled,model,expected", [
    (["qwen3:1.7b"], "qwen3:1.7b", True),
    (["qwen3:0.6b"], "qwen3:1.7b", False),
    (["nomic-embed-text:latest"], "nomic-embed-text", True),
    (["qwen3:1.7b-q8_0"], "qwen3:1.7b", False),
])
def test_model_availability_matches_exact_names(pulled, model, expected):
    fake = FakeOllama()
    fake.pulled = pulled
    assert chat(fake, llm_model=model).available() is expected


@pytest.mark.parametrize("fake", [FakeOllama(status=500), FakeOllama(raise_exc=httpx.ConnectError("down")), FakeOllama(raise_exc=httpx.ReadTimeout("slow"))])
def test_an_unreachable_ollama_is_reported_not_ready_without_raising(fake):
    assert embedder(fake).available() is False
    assert chat(fake).available() is False


def test_cpu_threads_follow_the_container_limit_when_configured():
    fake = FakeOllama()
    embedder(fake, ollama_num_thread=2).embed(["x"])
    chat(fake, ollama_num_thread=2).complete([{"role": "user", "content": "x"}], 16)
    embed_body, chat_body = (json.loads(r.content) for r in fake.requests)
    assert embed_body["options"] == {"num_thread": 2}
    assert chat_body["options"]["num_thread"] == 2
    with pytest.raises(ValueError, match="RAG_OLLAMA_NUM_THREAD must be between 1 and 64"):
        local_settings(ollama_num_thread=100).validate()


# ------------------------------------------------------------------ timings and thinking (latency diagnostics)

def test_ollama_timings_are_reported_in_milliseconds_and_reach_the_answer_log():
    from app.answering import AnswerOutcome
    from app.main import timing_detail

    body = {"model": CHAT_MODEL, "message": {"role": "assistant", "content": "Hiệu suất 22,5% [1]"}, "done": True, "prompt_eval_count": 2370,
            "eval_count": 60, "load_duration": 1_500_000_000, "prompt_eval_duration": 40_000_000_000, "eval_duration": 8_000_000_000,
            "total_duration": 49_600_000_000}
    completion = chat(FakeOllama(chat_body=json.dumps(body).encode())).complete([{"role": "user", "content": "x"}], 100)
    assert completion.timings == {"loadMs": 1500, "promptEvalMs": 40000, "evalMs": 8000, "totalMs": 49600}
    assert (completion.prompt_tokens, completion.completion_tokens) == (2370, 60)
    assert timing_detail(AnswerOutcome("a", True, completion_tokens=60, timings=completion.timings)) == \
        ", load=1500ms, promptEval=40000ms, eval=8000ms, 7.5 tok/s"
    # Nothing reported: no numbers are invented.
    assert chat(FakeOllama()).complete([{"role": "user", "content": "x"}], 100).timings is None
    assert timing_detail(AnswerOutcome(None, False)) == ""


@pytest.mark.parametrize("options", [None, ("qwen3:1.7b", 0.0, "max_completion_tokens"), ("qwen3:1.7b", None, "max_tokens")])
def test_thinking_stays_off_whatever_generation_options_the_api_sends(options):
    from app.llm import GenerationOptions

    generation = None if options is None else GenerationOptions(*options)
    body = chat(FakeOllama()).payload([{"role": "user", "content": "x"}], 50, generation)
    assert body["think"] is False
    assert "max_tokens" not in body and "max_completion_tokens" not in body     # OpenAI names never leak into the native API
    assert body["options"]["num_predict"] == 50


def test_the_openai_compatible_payload_is_untouched_by_the_ollama_adapter():
    from app.llm import OpenAICompatibleChatModel

    model = OpenAICompatibleChatModel(local_settings(llm_provider="openai", llm_base_url="https://api.example.com/v1", llm_api_key="k"))
    body = model.payload([{"role": "user", "content": "x"}], 50)
    assert "think" not in body and "options" not in body and "keep_alive" not in body
    assert body["max_tokens"] == 50
