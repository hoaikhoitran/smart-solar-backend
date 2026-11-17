"""OpenAI cloud provider: 1024-dimension embeddings, the gpt-6-luna Chat Completions payload, usage for cost accounting, billing
status of failures, and the embedding-model migration (separate query embedder). Every provider is a mock transport; the only
network test is the opt-in, cost-capped live smoke test at the end (RAG_LIVE_OPENAI_SMOKE=1)."""

import json
import os
import time
import uuid

import httpx
import pytest
from fastapi.testclient import TestClient

from app.answering import DeadlineExceeded, answer_question
from app.config import Settings
from app.embeddings import (EmbeddingContractError, HashingEmbedder, OpenAIEmbedder, ProviderError, ProviderRateLimited, ProviderTimeout,
                            create_query_embedder)
from app.ingestion import ingest
from app.llm import GenerationOptions, OpenAICompatibleChatModel
from app.main import build_services, create_app
from app.security import sign
from app.storage import LocalStorage
from app.vectorstore import InMemoryVectorStore, StoredChunk
from tests.conftest import SECRET, sha

EMBED = "text-embedding-3-small"
LUNA = "gpt-6-luna"


def cloud_settings(**overrides):
    values = dict(shared_secret=SECRET, storage="local", embedding_provider="openai", embedding_model=EMBED, embedding_dimension=1024,
                  embedding_base_url="https://api.invalid/v1", embedding_api_key="sk-test", llm_provider="openai", llm_model=LUNA,
                  llm_base_url="https://api.invalid/v1", llm_api_key="sk-test")
    values.update(overrides)
    return Settings(**values)


class FakeEmbeddings:
    def __init__(self, dimension=1024, usage=True, status=200, raise_exc=None):
        self.dimension, self.usage, self.status, self.raise_exc = dimension, usage, status, raise_exc
        self.requests = []

    def __call__(self, request):
        body = json.loads(request.content)
        self.requests.append(body)
        if self.raise_exc:
            raise self.raise_exc
        if self.status != 200:
            return httpx.Response(self.status, json={"error": {"message": "secret details"}})
        data = [{"index": i, "embedding": [((i + j) % 7 + 1) / 10 for j in range(self.dimension)]} for i, _ in enumerate(body["input"])]
        reply = {"model": EMBED, "data": data}
        if self.usage:
            reply["usage"] = {"prompt_tokens": 11 * len(body["input"]), "total_tokens": 11 * len(body["input"])}
        return httpx.Response(200, json=reply)


def openai_embedder(fake, **overrides):
    return OpenAIEmbedder(cloud_settings(**overrides), transport=httpx.MockTransport(fake))


def chat_model(handler):
    return OpenAICompatibleChatModel(cloud_settings(), transport=httpx.MockTransport(handler))


# ---------------------------------------------------------------- embeddings: 1024 dimensions + usage

def test_text_embedding_3_small_validates_at_1536_and_1024_and_refuses_other_sizes():
    cloud_settings(embedding_dimension=1024).validate()
    cloud_settings(embedding_dimension=1536).validate()
    for size in (768, 512, 3072):
        with pytest.raises(ValueError, match="RAG_EMBEDDING_DIMENSION must be 1536 or 1024"):
            cloud_settings(embedding_dimension=size, vector_store="memory").validate()
    with pytest.raises(ValueError, match="RAG_EMBEDDING_MODEL must be one of"):
        cloud_settings(embedding_model="text-embedding-3-large").validate()


def test_requests_ask_for_1024_dimensions_and_usage_is_reported():
    fake = FakeEmbeddings()
    result = openai_embedder(fake).embed_with_usage(["a", "b", "c"])
    assert fake.requests[0] == {"model": EMBED, "input": ["a", "b", "c"], "dimensions": 1024, "encoding_format": "float"}
    assert len(result.vectors) == 3 and all(len(v) == 1024 for v in result.vectors)
    assert result.tokens == 33


def test_missing_usage_is_estimated_conservatively_and_a_wrong_dimension_never_passes():
    result = openai_embedder(FakeEmbeddings(usage=False)).embed_with_usage(["x" * 300])
    assert result.tokens == 100
    with pytest.raises(EmbeddingContractError, match="expected 1024"):
        openai_embedder(FakeEmbeddings(dimension=1536)).embed(["a"])


@pytest.mark.parametrize("fake,kind,billing", [
    (FakeEmbeddings(status=429), ProviderRateLimited, "none"),
    (FakeEmbeddings(status=500), ProviderError, "none"),
    (FakeEmbeddings(raise_exc=httpx.ReadTimeout("slow")), ProviderTimeout, "uncertain"),
    (FakeEmbeddings(raise_exc=httpx.ConnectError("refused")), ProviderError, "none"),
    (FakeEmbeddings(raise_exc=httpx.RemoteProtocolError("dropped")), ProviderError, "uncertain"),
])
def test_failures_say_whether_the_provider_may_have_billed(fake, kind, billing):
    with pytest.raises(kind) as caught:
        openai_embedder(fake).embed(["a"])
    assert caught.value.billing == billing
    assert "secret details" not in str(caught.value) and "sk-test" not in str(caught.value)


def test_ingestion_reports_its_billable_embedding_tokens(tmp_path):
    settings = cloud_settings(storage="local", local_storage_dir=str(tmp_path), chunk_characters=400, chunk_overlap=60)
    body = ("Tấm pin được bảo hành 12 năm. " * 60).encode()
    path = tmp_path / "kb" / "doc.txt"
    path.parent.mkdir(parents=True)
    path.write_bytes(body)
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    request = {"versionId": version, "documentId": document, "bucket": "kb", "objectKey": "doc.txt", "contentType": "text/plain",
               "expectedSha256": sha(body), "title": "T", "versionNumber": 1}
    fake = FakeEmbeddings()
    outcome = ingest(settings, LocalStorage(str(tmp_path)), openai_embedder(fake), InMemoryVectorStore(1024), request)
    assert outcome.status == "READY"
    assert outcome.embedding_tokens == 11 * sum(len(r["input"]) for r in fake.requests) > 0
    failed = ingest(settings, LocalStorage(str(tmp_path)), openai_embedder(FakeEmbeddings(raise_exc=httpx.ReadTimeout("slow"))),
                    InMemoryVectorStore(1024), request)
    assert (failed.status, failed.billing) == ("FAILED", "uncertain")


# ---------------------------------------------------------------- gpt-6-luna generation payload

def test_gpt_6_luna_payload_turns_reasoning_off_bounds_output_and_never_sends_temperature_with_reasoning():
    captured = []

    def handler(request):
        captured.append(json.loads(request.content))
        return httpx.Response(200, json={"model": LUNA, "choices": [{"message": {"content": "Đáp án [1]"}}], "usage": {
            "prompt_tokens": 1200, "completion_tokens": 80, "prompt_tokens_details": {"cached_tokens": 1024},
            "completion_tokens_details": {"reasoning_tokens": 0}}})

    model = chat_model(handler)
    messages = [{"role": "user", "content": "hi"}]
    completion = model.complete(messages, 300, GenerationOptions(LUNA, None, "max_completion_tokens", "none"))
    assert captured[-1] == {"model": LUNA, "messages": messages, "max_completion_tokens": 300, "reasoning_effort": "none"}
    assert (completion.prompt_tokens, completion.completion_tokens, completion.cached_prompt_tokens, completion.reasoning_tokens) == (1200, 80, 1024, 0)
    assert completion.usage_reported and completion.provider == "openai"

    # With reasoning on, a temperature is never sent (GPT-6 models reject it unless reasoning is "none").
    model.complete(messages, 300, GenerationOptions(LUNA, 0.3, "max_completion_tokens", "low"))
    assert "temperature" not in captured[-1] and captured[-1]["reasoning_effort"] == "low"
    model.complete(messages, 300, GenerationOptions("gpt-4.1-mini", 0.3, "max_tokens"))
    assert captured[-1]["temperature"] == 0.3 and "reasoning_effort" not in captured[-1]
    with pytest.raises(ValueError, match="reasoning effort"):
        model.complete(messages, 300, GenerationOptions(LUNA, None, "max_completion_tokens", "turbo"))


def test_missing_usage_assumes_the_whole_output_budget_and_is_flagged():
    model = chat_model(lambda r: httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}]}))
    completion = model.complete([{"role": "user", "content": "x" * 90}], 300, GenerationOptions(LUNA, None, "max_completion_tokens", "none"))
    assert (completion.usage_reported, completion.completion_tokens) == (False, 300)
    assert completion.prompt_tokens == 30


@pytest.mark.parametrize("handler,kind,billing", [
    (lambda r: httpx.Response(429, json={}), ProviderRateLimited, "none"),
    (lambda r: httpx.Response(500, json={}), ProviderError, "none"),
    (lambda r: (_ for _ in ()).throw(httpx.ReadTimeout("slow")), ProviderTimeout, "uncertain"),
    (lambda r: httpx.Response(200, content=b"<html>"), ProviderError, "uncertain"),
])
def test_generation_failures_are_classified_and_never_retried_with_another_model(handler, kind, billing):
    calls = []

    def counting(request):
        calls.append(json.loads(request.content)["model"])
        return handler(request)

    with pytest.raises(kind) as caught:
        chat_model(counting).complete([{"role": "user", "content": "q"}], 100, GenerationOptions(LUNA, None, "max_completion_tokens", "none"))
    assert caught.value.billing == billing
    assert calls == [LUNA]


# ---------------------------------------------------------------- answer: usage, no-evidence short cut, failures

class CountingModel:
    provider = "openai"
    model = LUNA

    def __init__(self, error=None):
        self.calls, self.error = 0, error

    def complete(self, messages, max_tokens, options=None, timeout_seconds=None):
        self.calls += 1
        if self.error:
            raise self.error
        from app.llm import Completion
        return Completion("Bảo hành 12 năm [1]", 900, 40, LUNA, cached_prompt_tokens=512, reasoning_tokens=0, provider="openai")


def chunk(version, document, model, vector, text="Tấm pin được bảo hành 12 năm."):
    return StoredChunk(f"{version}:{model}:0", version, document, 0, None, None, text, "0" * 64, vector, model)


def test_no_evidence_skips_generation_and_still_reports_the_question_embedding(settings):
    store = InMemoryVectorStore()
    model = CountingModel()
    embedder = openai_embedder(FakeEmbeddings())
    outcome = answer_question(settings, embedder, store, model, "q", [], [], 4, 1000, 300)
    assert (outcome.grounded, model.calls, outcome.generation_provider) == (False, 0, None)
    assert (outcome.embedding_tokens, outcome.embedding_provider, outcome.embedding_model) == (11, "openai", EMBED)


def test_answer_reports_generation_usage_and_a_failure_carries_the_embedding_already_spent(settings):
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    embedder = openai_embedder(FakeEmbeddings())
    store = InMemoryVectorStore()
    store.replace_version(version, [chunk(version, document, EMBED, embedder.embed(["q"])[0])])
    allowed = [{"versionId": version, "documentId": document, "title": "Bảo hành", "versionNumber": 1}]
    outcome = answer_question(settings, embedder, store, CountingModel(), "q", [], allowed, 4, 1000, 300, min_score=0.0)
    assert outcome.grounded and (outcome.prompt_tokens, outcome.completion_tokens, outcome.cached_prompt_tokens) == (900, 40, 512)
    assert outcome.generation_provider == "openai" and outcome.embedding_tokens == 11

    with pytest.raises(ProviderTimeout) as caught:
        answer_question(settings, embedder, store, CountingModel(ProviderTimeout("slow")), "q", [], allowed, 4, 1000, 300, min_score=0.0)
    assert (caught.value.billing, caught.value.embedding_tokens) == ("uncertain", 11)
    with pytest.raises(DeadlineExceeded) as cut:
        answer_question(settings, embedder, store, CountingModel(ProviderTimeout("slow")), "q", [], allowed, 4, 1000, 300, min_score=0.0,
                        deadline=time.monotonic() + 60)
    assert (cut.value.billing, cut.value.embedding_tokens) == ("uncertain", 11)


def test_the_answer_endpoint_returns_billing_status_and_spent_tokens_on_failure(settings, tmp_path):
    embedder = openai_embedder(FakeEmbeddings())
    store = InMemoryVectorStore()
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    store.replace_version(version, [chunk(version, document, EMBED, embedder.embed(["q"])[0])])
    services = build_services(settings, storage=LocalStorage(str(tmp_path)), embedder=embedder, store=store,
                              model=CountingModel(ProviderRateLimited("rate limited (HTTP 429)")))
    client = TestClient(create_app(services))
    body = json.dumps({"question": "q", "allowedVersions": [{"versionId": version, "documentId": document, "title": "T", "versionNumber": 1}],
                       "minScore": 0, "generationModel": LUNA, "tokenParameter": "max_completion_tokens", "reasoningEffort": "none",
                       "temperature": None}).encode()
    timestamp = str(int(time.time()))
    signature = sign(SECRET, timestamp, body)
    response = client.post("/internal/v1/answer", content=body, headers={"X-SmartSolar-Timestamp": timestamp, "X-SmartSolar-Signature": signature,
                                                                         "Content-Type": "application/json"})
    assert response.status_code == 503
    assert response.json() == {"detail": "provider rate limited", "billing": "none", "usage": {"embeddingTokens": 11, "embeddingProvider": "openai"}}


# ---------------------------------------------------------------- embedding-model migration (query embedder)

def test_a_query_embedder_keeps_questions_on_the_old_model_while_the_new_one_is_reindexed(settings):
    migrating = cloud_settings(vector_store="memory", query_embedding_provider="hashing", query_embedding_model="hashing-dev",
                               environment="development", embedding_dimension=1024)
    migrating.validate()
    query = migrating.query_settings()
    assert (query.embedding_provider, query.embedding_model, query.embedding_dimension) == ("hashing", "hashing-dev", 1024)
    assert create_query_embedder(migrating, object()).model == "hashing-dev"
    with pytest.raises(ValueError, match="must differ"):
        cloud_settings(query_embedding_model=EMBED).validate()

    old, new = HashingEmbedder(1024), openai_embedder(FakeEmbeddings())
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    store = InMemoryVectorStore(1024)
    store.replace_version(version, [chunk(version, document, old.model, old.embed(["bảo hành"])[0])])
    store.replace_version(version, [chunk(version, document, new.model, new.embed(["bảo hành"])[0], "Phiên bản OpenAI")])
    allowed = [{"versionId": version, "documentId": document, "title": "T", "versionNumber": 1}]
    outcome = answer_question(settings, new, store, CountingModel(), "bảo hành", [], allowed, 4, 1000, 300, min_score=0.0, query_embedder=old)
    # Only the old model's chunk is searched and cited; the reindexed (new-model) chunk is not served before the switch.
    assert outcome.embedding_model == "hashing-dev" and outcome.embedding_tokens == 0
    assert [c["chunkId"] for c in outcome.citations] == [f"{version}:hashing-dev:0"]


# ---------------------------------------------------------------- opt-in live smoke test (paid, cost-capped)

LIVE = os.environ.get("RAG_LIVE_OPENAI_SMOKE") == "1" and os.environ.get("RAG_EMBEDDING_API_KEY") and os.environ.get("RAG_LLM_API_KEY")
# Standard prices per 1M tokens (developers.openai.com/api/docs/pricing, read 2026-11-17).
PRICE = {"embed": 0.02, "in": 0.10, "cached": 0.01, "out": 0.50}
CAP_USD = 0.002


@pytest.mark.skipif(not LIVE, reason="paid live smoke test: set RAG_LIVE_OPENAI_SMOKE=1 and the API keys")
def test_live_openai_1024_dimension_embedding_and_gpt_6_luna_answer_within_a_tiny_budget():
    settings = cloud_settings(embedding_base_url="https://api.openai.com/v1", embedding_api_key=os.environ["RAG_EMBEDDING_API_KEY"],
                              llm_base_url="https://api.openai.com/v1", llm_api_key=os.environ["RAG_LLM_API_KEY"], provider_timeout_seconds=30)
    # Upper bound before any call: 2 short inputs (< 50 tokens) + a 200-token prompt with at most 40 output tokens.
    assert (50 * PRICE["embed"] + 200 * PRICE["in"] + 40 * PRICE["out"]) / 1_000_000 < CAP_USD
    result = OpenAIEmbedder(settings).embed_with_usage(["Tấm pin được bảo hành bao lâu?", "Inverter bảo hành 5 năm."])
    assert [len(v) for v in result.vectors] == [1024, 1024] and 0 < result.tokens < 50
    model = OpenAICompatibleChatModel(settings)
    completion = model.complete([{"role": "system", "content": "Trả lời đúng một từ: OK"}, {"role": "user", "content": "Kiểm tra"}], 40,
                                GenerationOptions(LUNA, None, "max_completion_tokens", "none"))
    assert completion.usage_reported and 0 < completion.prompt_tokens < 200 and completion.completion_tokens <= 40
    assert completion.model.startswith(LUNA)
    spent = (result.tokens * PRICE["embed"] + (completion.prompt_tokens - completion.cached_prompt_tokens) * PRICE["in"]
             + completion.cached_prompt_tokens * PRICE["cached"] + completion.completion_tokens * PRICE["out"]) / 1_000_000
    print(f"LIVE embedding_tokens={result.tokens} prompt={completion.prompt_tokens} cached={completion.cached_prompt_tokens} "
          f"completion={completion.completion_tokens} reasoning={completion.reasoning_tokens} model={completion.model} usd={spent:.8f}")
    assert spent < CAP_USD
