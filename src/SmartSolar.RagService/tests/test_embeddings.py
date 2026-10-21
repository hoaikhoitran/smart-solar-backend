"""Embedding configuration and provider contract (OpenAI text-embedding-3-small, 1536 dimensions).

Every OpenAI call here goes to an httpx.MockTransport: no network, no paid tokens. The live smoke test at
the end is opt-in (RAG_LIVE_EMBEDDING_SMOKE=1 plus a real RAG_EMBEDDING_API_KEY) and skipped otherwise.
"""

import json
import math
import os

import httpx
import pytest

from app.answering import answer_question
from app.config import DEFAULT_OPENAI_BASE_URL, SCHEMA_EMBEDDING_DIMENSION, Settings
from app.embeddings import EmbeddingContractError, HashingEmbedder, OpenAIEmbedder, ProviderError, create_embedder
from app.ingestion import FAILED, ingest
from app.storage import LocalStorage
from app.vectorstore import EmbeddingDimensionError, InMemoryVectorStore, StoredChunk
from tests.conftest import SECRET, sha

MODEL = "text-embedding-3-small"


def openai_settings(**overrides):
    values = dict(shared_secret=SECRET, storage="local", embedding_provider="openai", embedding_model=MODEL,
                  embedding_dimension=1536, embedding_base_url="https://embeddings.test/v1", embedding_api_key="test-key-not-real",
                  embedding_batch_size=64)
    values.update(overrides)
    return Settings(**values)


class FakeOpenAI:
    """Answers /embeddings like OpenAI does; lets a test break the contract on purpose."""

    def __init__(self, dimension=1536, model=MODEL, status=200, count_delta=0, poison=None, raise_exc=None, body=None):
        self.requests = []
        self.dimension, self.model, self.status = dimension, model, status
        self.count_delta, self.poison, self.raise_exc, self.body = count_delta, poison, raise_exc, body
        self._vectors = HashingEmbedder(dimension, "fake")

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        if self.raise_exc is not None:
            raise self.raise_exc
        if self.status != 200:
            return httpx.Response(self.status, json={"error": {"message": "nope"}})
        if self.body is not None:
            return httpx.Response(200, content=self.body)
        texts = json.loads(request.content)["input"]
        vectors = self._vectors.embed(texts)
        if self.poison is not None:
            vectors[0][0] = self.poison
        data = [{"object": "embedding", "index": i, "embedding": v} for i, v in enumerate(vectors[: len(vectors) + self.count_delta])]
        body = {"object": "list", "model": self.model, "data": data, "usage": {"prompt_tokens": 1, "total_tokens": 1}}
        # json.dumps (unlike httpx's json=) can emit NaN, as a misbehaving provider might.
        return httpx.Response(200, content=json.dumps(body).encode(), headers={"content-type": "application/json"})


def embedder(fake, **overrides):
    return OpenAIEmbedder(openai_settings(**overrides), transport=httpx.MockTransport(fake))


# ---------------------------------------------------------------- configuration

def test_environment_defaults_to_text_embedding_3_small_with_1536_dimensions(monkeypatch):
    for name in list(os.environ):
        if name.startswith("RAG_"):
            monkeypatch.delenv(name)
    settings = Settings.from_env()
    assert settings.embedding_provider == "openai"
    assert settings.embedding_model == MODEL
    assert settings.embedding_dimension == SCHEMA_EMBEDDING_DIMENSION == 1536
    assert settings.embedding_base_url == DEFAULT_OPENAI_BASE_URL
    # Without an API key the production default refuses to start instead of falling back to the stand-in.
    monkeypatch.setenv("RAG_SHARED_SECRET", SECRET)
    monkeypatch.setenv("RAG_STORAGE", "local")
    with pytest.raises(ValueError, match="RAG_EMBEDDING_API_KEY"):
        Settings.from_env().validate()


def test_the_correct_model_and_dimension_validate():
    openai_settings().validate()
    openai_settings(vector_store="pgvector", database_url="postgresql://localhost/x").validate()


@pytest.mark.parametrize("model,dimension,message", [
    (MODEL, 3072, "RAG_EMBEDDING_DIMENSION must be 1536"),
    (MODEL, 512, "RAG_EMBEDDING_DIMENSION must be 1536"),
    ("text-embedding-3-large", 3072, "RAG_EMBEDDING_MODEL must be one of"),
    ("text-embedding-ada-002", 1536, "RAG_EMBEDDING_MODEL must be one of"),
])
def test_models_or_dimensions_that_do_not_match_the_schema_are_refused(model, dimension, message):
    with pytest.raises(ValueError, match=message):
        openai_settings(embedding_model=model, embedding_dimension=dimension).validate()


def test_the_offline_stand_in_is_isolated_from_production():
    with pytest.raises(ValueError, match="only allowed with RAG_ENV=development or test"):
        Settings(shared_secret=SECRET, storage="local", embedding_provider="hashing", environment="staging").validate()
    with pytest.raises(ValueError, match="production"):
        Settings(shared_secret=SECRET, storage="s3", s3_allowed_buckets=["b"], vector_store="pgvector", database_url="postgresql://x/y",
                 embedding_provider="hashing", environment="production", llm_provider="openai", llm_base_url="https://l", llm_api_key="k").validate()
    with pytest.raises(ValueError, match="must be hashing-dev"):
        Settings(shared_secret=SECRET, storage="local", embedding_provider="hashing", embedding_model=MODEL).validate()
    with pytest.raises(ValueError, match="match knowledge_chunk.embedding"):
        Settings(shared_secret=SECRET, storage="local", embedding_provider="hashing", embedding_dimension=256,
                 vector_store="pgvector", database_url="postgresql://x/y").validate()
    assert isinstance(create_embedder(openai_settings()), OpenAIEmbedder)


def test_the_llm_configuration_is_untouched_by_the_embedding_rules():
    settings = openai_settings(llm_provider="extractive")
    settings.validate()
    assert settings.llm_provider == "extractive"


# ---------------------------------------------------------------- provider contract

def test_requests_name_the_model_and_ask_for_1536_float_dimensions():
    fake = FakeOpenAI()
    vectors = embedder(fake).embed(["bảo hành tấm pin", "inverter"])

    assert [len(v) for v in vectors] == [1536, 1536]
    request = fake.requests[0]
    assert request.url == httpx.URL("https://embeddings.test/v1/embeddings")
    assert request.headers["authorization"] == "Bearer test-key-not-real"
    assert json.loads(request.content) == {"model": MODEL, "input": ["bảo hành tấm pin", "inverter"], "dimensions": 1536, "encoding_format": "float"}


def test_large_inputs_are_sent_in_batches():
    fake = FakeOpenAI()
    assert len(embedder(fake, embedding_batch_size=64).embed([f"t{i}" for i in range(130)])) == 130
    assert [len(json.loads(r.content)["input"]) for r in fake.requests] == [64, 64, 2]


@pytest.mark.parametrize("fake,message", [
    (FakeOpenAI(dimension=3072), "expected 1536"),
    (FakeOpenAI(dimension=1535), "expected 1536"),
    (FakeOpenAI(model="text-embedding-3-large"), "expected 'text-embedding-3-small'"),
    (FakeOpenAI(count_delta=-1), "unexpected number of vectors"),
    (FakeOpenAI(poison=math.nan), "non-finite"),
    (FakeOpenAI(poison="x"), "non-numeric"),
    (FakeOpenAI(body=b"<html>gateway</html>"), "non-JSON"),
])
def test_responses_that_break_the_contract_are_rejected_before_storage(fake, message):
    with pytest.raises(EmbeddingContractError, match=message):
        embedder(fake).embed(["a", "b"])


@pytest.mark.parametrize("fake,message", [
    (FakeOpenAI(raise_exc=httpx.ReadTimeout("slow")), "timed out"),
    (FakeOpenAI(raise_exc=httpx.ConnectError("down")), "unreachable"),
    (FakeOpenAI(status=429), "HTTP 429"),
    (FakeOpenAI(status=500), "HTTP 500"),
    (FakeOpenAI(status=401), "HTTP 401"),
])
def test_timeouts_and_provider_errors_are_reported_without_secrets(fake, message):
    with pytest.raises(ProviderError, match=message) as error:
        embedder(fake).embed(["a"])
    assert "test-key-not-real" not in str(error.value)


def test_a_provider_failure_fails_the_ingestion_instead_of_switching_models(settings, ids, tmp_path):
    body = ("Hệ thống điện mặt trời áp mái. " * 40).encode()
    path = tmp_path / "kb" / "knowledge" / "doc.txt"
    path.parent.mkdir(parents=True)
    path.write_bytes(body)
    store = InMemoryVectorStore(1536)
    request = {"versionId": ids[0], "documentId": ids[1], "bucket": "kb", "objectKey": "knowledge/doc.txt", "contentType": "text/plain",
               "expectedSha256": sha(body), "title": "T", "versionNumber": 1}

    outcome = ingest(settings, LocalStorage(str(tmp_path)), embedder(FakeOpenAI(status=503)), store, request)
    assert outcome.status == FAILED and "HTTP 503" in outcome.error
    outcome = ingest(settings, LocalStorage(str(tmp_path)), embedder(FakeOpenAI(dimension=768)), store, request)
    assert outcome.status == FAILED and "expected 1536" in outcome.error
    assert store.count(ids[0]) == 0
    ok = ingest(settings, LocalStorage(str(tmp_path)), embedder(FakeOpenAI()), store, request)
    assert ok.status == "READY" and ok.embedding_model == MODEL and ok.embedding_dimension == 1536


# ---------------------------------------------------------------- store guards, consistency, reindex

def chunk(version, model, vector):
    return StoredChunk(f"{version}:0", version, "d1", 0, 1, None, "bảo hành tấm pin 12 năm", "0" * 64, vector, model)


def test_vectors_of_the_wrong_dimension_never_reach_the_store():
    store = InMemoryVectorStore(1536)
    with pytest.raises(EmbeddingDimensionError):
        store.replace_version("v1", [chunk("v1", MODEL, [0.1] * 768)])
    with pytest.raises(EmbeddingDimensionError):
        store.search([0.1] * 768, ["v1"], 3, MODEL)
    with pytest.raises(EmbeddingDimensionError, match="one embedding model"):
        store.replace_version("v1", [chunk("v1", MODEL, [0.1] * 1536), StoredChunk("v1:1", "v1", "d1", 1, 1, None, "x", "0" * 64, [0.1] * 1536, "other")])


def test_documents_and_queries_use_the_same_model_and_models_never_mix(settings):
    real = embedder(FakeOpenAI())
    store = InMemoryVectorStore(1536)
    text = "Tấm pin mặt trời được bảo hành sản phẩm 12 năm."
    store.replace_version("v1", [chunk("v1", real.model, real.embed([text])[0])])
    allowed = [{"versionId": "v1", "documentId": "d1", "title": "Bảo hành", "versionNumber": 1}]
    settings.embedding_dimension, settings.min_score = 1536, 0.0

    from app.llm import ExtractiveChatModel
    hit = answer_question(settings, real, store, ExtractiveChatModel(), text, [], allowed, 3, 2000, 300)
    assert hit.grounded and hit.citations[0]["versionId"] == "v1"
    # A query embedded with another model (same dimension) does not retrieve these chunks.
    other = HashingEmbedder(1536, "hashing-dev")
    miss = answer_question(settings, other, store, ExtractiveChatModel(), text, [], allowed, 3, 2000, 300)
    assert not miss.grounded and miss.citations == []


def test_reindexing_after_a_model_change_replaces_the_version_and_restores_retrieval(settings, ids, tmp_path):
    body = ("Tấm pin mặt trời được bảo hành 12 năm. " * 30).encode()
    path = tmp_path / "kb" / "knowledge" / "doc.txt"
    path.parent.mkdir(parents=True)
    path.write_bytes(body)
    request = {"versionId": ids[0], "documentId": ids[1], "bucket": "kb", "objectKey": "knowledge/doc.txt", "contentType": "text/plain",
               "expectedSha256": sha(body), "title": "T", "versionNumber": 1}
    store = InMemoryVectorStore(1536)
    old = HashingEmbedder(1536, "hashing-dev")          # indexed with an earlier model
    assert ingest(settings, LocalStorage(str(tmp_path)), old, store, request).status == "READY"
    new = embedder(FakeOpenAI())
    query = new.embed(["bảo hành"])[0]
    assert store.search(query, [ids[0]], 5, new.model) == []   # not retrievable until reindexed

    assert ingest(settings, LocalStorage(str(tmp_path)), new, store, request).status == "READY"   # admin reindex
    hits = store.search(query, [ids[0]], 5, new.model)
    assert hits and all(h.version_id == ids[0] for h in hits)
    assert store.search(query, [ids[0]], 5, old.model) == []   # old-model chunks were replaced


# ---------------------------------------------------------------- opt-in live smoke test (paid)

@pytest.mark.skipif(os.environ.get("RAG_LIVE_EMBEDDING_SMOKE") != "1" or not os.environ.get("RAG_EMBEDDING_API_KEY"),
                    reason="Live OpenAI call: set RAG_LIVE_EMBEDDING_SMOKE=1 and RAG_EMBEDDING_API_KEY to run (costs a few tokens)")
def test_live_openai_returns_1536_dimensions():
    settings = openai_settings(embedding_base_url=os.environ.get("RAG_EMBEDDING_BASE_URL") or DEFAULT_OPENAI_BASE_URL,
                               embedding_api_key=os.environ["RAG_EMBEDDING_API_KEY"])
    vector = OpenAIEmbedder(settings).embed(["SmartSolar smoke test"])[0]
    assert len(vector) == 1536
