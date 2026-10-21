"""Per-request RAG configuration: validation, defaults, provider payloads, chunking and safe replacement."""

import json
import os
import time
import uuid

import httpx
import pytest
from fastapi.testclient import TestClient

from app.answering import answer_question
from app.config import Settings
from app.embeddings import HashingEmbedder, ProviderError
from app.ingestion import FAILED, ingest
from app.llm import ExtractiveChatModel, GenerationOptions, OpenAICompatibleChatModel
from app.main import build_services, create_app
from app.prompting import SYSTEM_PROMPT, UnknownPromptVersion, build_messages
from app.security import sign
from app.storage import LocalStorage
from app.vectorstore import InMemoryVectorStore, StoredChunk
from tests.conftest import SECRET, sha

TEXT = ("Tấm pin mặt trời được bảo hành sản phẩm 12 năm và bảo hành hiệu suất 25 năm. " * 6 + "\n\n") * 3
BODY = ("Hệ thống điện mặt trời áp mái giúp giảm hóa đơn tiền điện và bảo vệ môi trường. " * 30 + "\n\n") * 6


class RecordingModel(ExtractiveChatModel):
    def __init__(self):
        super().__init__()
        self.options = []
        self.max_tokens = []
        self.system = []

    def complete(self, messages, max_tokens, options=None):
        self.options.append(options)
        self.max_tokens.append(max_tokens)
        self.system.append(messages[0]["content"])
        return super().complete(messages, max_tokens, options)


def post(client, path, payload):
    body = json.dumps(payload).encode()
    ts = str(int(time.time()))
    return client.post(path, content=body, headers={"Content-Type": "application/json", "X-SmartSolar-Timestamp": ts,
                                                    "X-SmartSolar-Signature": sign(SECRET, ts, body)})


def put(settings, key, data, bucket="kb"):
    path = os.path.join(settings.local_storage_dir, bucket, key)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(data)


def ingest_request(version, document, key, data, **extra):
    request = {"versionId": version, "documentId": document, "bucket": "kb", "objectKey": key, "contentType": "text/plain",
               "expectedSha256": sha(data), "title": "Bảo hành", "versionNumber": 1}
    request.update(extra)
    return request


@pytest.fixture
def world(settings):
    model = RecordingModel()
    store = InMemoryVectorStore()
    client = TestClient(create_app(build_services(settings, store=store, model=model)))
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    key = f"knowledge/{uuid.uuid4()}/warranty.txt"
    put(settings, key, TEXT.encode())
    response = post(client, "/internal/v1/ingest", ingest_request(version, document, key, TEXT.encode()))
    assert response.status_code == 200 and response.json()["status"] == "READY", response.text
    allowed = [{"versionId": version, "documentId": document, "title": "Bảo hành", "versionNumber": 1}]
    return client, model, store, allowed


def ask(client, allowed, **extra):
    payload = {"question": "Tấm pin bảo hành bao lâu?", "history": [], "allowedVersions": allowed, "topK": 4, "maxContextTokens": 2000,
               "maxOutputTokens": 300}
    payload.update(extra)
    return post(client, "/internal/v1/answer", payload)


# ---------------------------------------------------------------- request validation


@pytest.mark.parametrize("extra", [
    {"generationModel": "gpt 4.1"},              # model id pattern
    {"generationModel": "x" * 101},
    {"temperature": 1.5},
    {"temperature": -0.1},
    {"tokenParameter": "max_output_tokens"},
    {"minScore": 1.2},
    {"configVersion": -1},
    {"promptVersion": "v999"},                   # not a known template
    {"promptVersion": ""},
    {"serviceSharedSecret": "x"},                # unknown fields are rejected
])
def test_invalid_answer_configuration_is_rejected(world, extra):
    client, model, _, allowed = world
    assert ask(client, allowed, **extra).status_code == 422
    assert model.options == []


@pytest.mark.parametrize("extra", [
    {"chunkCharacters": 100},                    # below the bound
    {"chunkCharacters": 9000},                   # above the bound
    {"chunkCharacters": 800, "chunkOverlap": 800},
    {"chunkCharacters": 800, "chunkOverlap": 900},
    {"chunkOverlap": -1},
    {"chunkOverlap": 3000},                      # with the environment chunk size (400 in tests)
    {"configVersion": -2},
    {"embeddingModel": "text-embedding-3-large"},  # not configurable per request
])
def test_invalid_ingest_configuration_is_rejected(settings, extra):
    store = InMemoryVectorStore()
    client = TestClient(create_app(build_services(settings, store=store)))
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    key = f"knowledge/{uuid.uuid4()}/doc.txt"
    put(settings, key, BODY.encode())
    assert post(client, "/internal/v1/ingest", ingest_request(version, document, key, BODY.encode(), **extra)).status_code == 422
    assert store.count(version) == 0


# ---------------------------------------------------------------- defaults and echo


def test_absent_fields_keep_the_environment_defaults(world, settings):
    client, model, _, allowed = world
    answer = ask(client, allowed)
    assert answer.status_code == 200, answer.text
    options = model.options[-1]
    assert options == GenerationOptions(settings.llm_model, settings.llm_temperature, "max_tokens")
    assert model.system[-1] == SYSTEM_PROMPT
    body = answer.json()
    assert body["configVersion"] is None and body["promptVersion"] == "v1"
    assert body["parameters"]["minScore"] == settings.min_score


def test_request_values_are_used_and_echoed(world, settings):
    client, model, _, allowed = world
    settings.max_output_tokens = 500
    answer = ask(client, allowed, configVersion=7, generationModel="gpt-4.1", temperature=0.3, tokenParameter="max_completion_tokens",
                 minScore=0.01, promptVersion="v1", maxOutputTokens=900)
    assert answer.status_code == 200, answer.text
    assert model.options[-1] == GenerationOptions("gpt-4.1", 0.3, "max_completion_tokens")
    # Environment ceilings still clamp the request.
    assert model.max_tokens[-1] == 500
    body = answer.json()
    assert body["grounded"] is True
    assert body["configVersion"] == 7
    assert body["parameters"] == {"generationModel": "gpt-4.1", "temperature": 0.3, "tokenParameter": "max_completion_tokens", "maxOutputTokens": 500,
                                  "maxContextTokens": 2000, "topK": 4, "minScore": 0.01}


def test_explicit_null_temperature_means_no_temperature(world):
    client, model, _, allowed = world
    assert ask(client, allowed, generationModel="o4-mini", temperature=None, tokenParameter="max_completion_tokens").status_code == 200
    assert model.options[-1].temperature is None


def test_min_score_from_the_request_filters_retrieval(world):
    client, model, _, allowed = world
    strict = ask(client, allowed, minScore=1.0).json()
    assert strict["grounded"] is False and strict["citations"] == []
    assert model.options == []   # no relevant source: the model is never called


def test_model_allowlist_from_the_environment(settings, world):
    client, model, _, allowed = world
    settings.llm_allowed_models = ["gpt-4.1-mini"]
    assert ask(client, allowed, generationModel="gpt-4o").status_code == 422
    assert ask(client, allowed, generationModel="gpt-4.1-mini").status_code == 200
    # Without a requested model the environment model is used (no allowlist check).
    assert ask(client, allowed).status_code == 200
    with pytest.raises(ValueError, match="RAG_LLM_ALLOWED_MODELS"):
        Settings(shared_secret=SECRET, storage="local", llm_allowed_models=["bad model"]).validate()


def test_unknown_prompt_versions_are_rejected_everywhere():
    with pytest.raises(UnknownPromptVersion):
        build_messages("Q", [], [], prompt_version="v2")
    assert build_messages("Q", [], [], prompt_version="v1")[0]["content"] == SYSTEM_PROMPT
    with pytest.raises(ValueError, match="RAG_PROMPT_VERSION"):
        Settings(shared_secret=SECRET, storage="local", prompt_version="v9").validate()


def test_answer_question_keeps_its_original_signature_defaults(settings):
    embedder = HashingEmbedder(settings.embedding_dimension)
    store = InMemoryVectorStore()
    text = "Tấm pin mặt trời được bảo hành sản phẩm 12 năm."
    store.replace_version("v1", [StoredChunk("v1:0", "v1", "d1", 0, 1, None, text, "h", embedder.embed([text])[0], embedder.model)])
    model = RecordingModel()
    allowed = [{"versionId": "v1", "documentId": "d1", "title": "T", "versionNumber": 1}]
    outcome = answer_question(settings, embedder, store, model, "Tấm pin bảo hành bao lâu?", [], allowed, 4, 2000, 300)
    assert outcome.grounded
    assert model.options == [None]


# ---------------------------------------------------------------- provider payload


def openai_model(captured):
    settings = Settings(llm_provider="openai", llm_model="gpt-4.1-mini", llm_base_url="https://llm.invalid/v1", llm_api_key="k", llm_temperature=0.1)
    model = OpenAICompatibleChatModel(settings)

    def handler(request):
        captured.append(json.loads(request.content))
        return httpx.Response(200, json={"choices": [{"message": {"content": "ok [1]"}}], "usage": {"prompt_tokens": 3, "completion_tokens": 2}})

    # A mock transport: nothing leaves the process.
    model._client = httpx.Client(transport=httpx.MockTransport(handler), base_url="https://llm.invalid/v1/")
    return model


def test_provider_payload_uses_the_token_parameter_and_omits_a_null_temperature():
    captured = []
    model = openai_model(captured)
    messages = [{"role": "user", "content": "hi"}]

    model.complete(messages, 300)
    assert captured[-1] == {"model": "gpt-4.1-mini", "messages": messages, "max_tokens": 300, "temperature": 0.1}

    model.complete(messages, 200, GenerationOptions("gpt-4.1", 0.4, "max_tokens"))
    assert captured[-1] == {"model": "gpt-4.1", "messages": messages, "max_tokens": 200, "temperature": 0.4}

    completion = model.complete(messages, 250, GenerationOptions("o4-mini", None, "max_completion_tokens"))
    assert captured[-1] == {"model": "o4-mini", "messages": messages, "max_completion_tokens": 250}
    assert "temperature" not in captured[-1] and "max_tokens" not in captured[-1]
    assert completion.model == "o4-mini"

    model.complete(messages, 100, GenerationOptions("gpt-4.1", 0.0, "max_tokens"))
    assert captured[-1]["temperature"] == 0.0   # zero is a value, not "absent"

    with pytest.raises(ValueError):
        model.complete(messages, 100, GenerationOptions("gpt-4.1", 0.1, "max_output_tokens"))


def test_provider_errors_are_still_reported():
    settings = Settings(llm_provider="openai", llm_model="m", llm_base_url="https://llm.invalid/v1", llm_api_key="k")
    model = OpenAICompatibleChatModel(settings)
    model._client = httpx.Client(transport=httpx.MockTransport(lambda r: httpx.Response(500)), base_url="https://llm.invalid/v1/")
    with pytest.raises(ProviderError):
        model.complete([{"role": "user", "content": "hi"}], 10, GenerationOptions("m", None, "max_completion_tokens"))


# ---------------------------------------------------------------- chunking and replacement


def test_request_chunk_settings_are_applied_and_echoed(settings, ids):
    put(settings, "knowledge/a/doc.txt", BODY.encode())
    storage = LocalStorage(settings.local_storage_dir)
    embedder = HashingEmbedder(settings.embedding_dimension)
    store = InMemoryVectorStore()

    small = ingest(settings, storage, embedder, store, ingest_request(*ids, "knowledge/a/doc.txt", BODY.encode(), configVersion=3, chunkCharacters=400,
                                                                      chunkOverlap=0))
    assert small.status == "READY"
    assert (small.config_version, small.chunk_characters, small.chunk_overlap) == (3, 400, 0)
    assert all(len(c.content) <= 400 for c in store._chunks[ids[0]])

    large = ingest(settings, storage, embedder, store, ingest_request(*ids, "knowledge/a/doc.txt", BODY.encode(), configVersion=4, chunkCharacters=4000,
                                                                      chunkOverlap=500))
    assert large.status == "READY" and large.chunk_count < small.chunk_count
    assert (large.config_version, large.chunk_characters, large.chunk_overlap) == (4, 4000, 500)
    # Replaced, not appended.
    assert store.count(ids[0]) == large.chunk_count

    default = ingest(settings, storage, embedder, store, ingest_request(*ids, "knowledge/a/doc.txt", BODY.encode()))
    assert (default.config_version, default.chunk_characters, default.chunk_overlap) == (None, settings.chunk_characters, settings.chunk_overlap)


def test_ingest_endpoint_echoes_the_configuration(settings):
    client = TestClient(create_app(build_services(settings, store=InMemoryVectorStore())))
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    key = f"knowledge/{uuid.uuid4()}/doc.txt"
    put(settings, key, BODY.encode())
    body = post(client, "/internal/v1/ingest", ingest_request(version, document, key, BODY.encode(), configVersion=2, chunkCharacters=1200,
                                                              chunkOverlap=150)).json()
    assert body["status"] == "READY"
    assert (body["configVersion"], body["chunkCharacters"], body["chunkOverlap"]) == (2, 1200, 150)


def test_retried_ingestion_with_the_same_snapshot_never_duplicates_chunks(settings, ids):
    put(settings, "knowledge/a/doc.txt", BODY.encode())
    storage = LocalStorage(settings.local_storage_dir)
    embedder = HashingEmbedder(settings.embedding_dimension)
    store = InMemoryVectorStore()
    request = ingest_request(*ids, "knowledge/a/doc.txt", BODY.encode(), configVersion=5, chunkCharacters=800, chunkOverlap=100)
    outcomes = [ingest(settings, storage, embedder, store, request) for _ in range(3)]
    assert {o.chunk_count for o in outcomes} == {outcomes[0].chunk_count}
    assert store.count(ids[0]) == outcomes[0].chunk_count
    assert len({c.chunk_id for c in store._chunks[ids[0]]}) == outcomes[0].chunk_count


def test_a_failed_reindex_leaves_the_previous_chunks_in_place(settings, ids):
    put(settings, "knowledge/a/doc.txt", BODY.encode())
    storage = LocalStorage(settings.local_storage_dir)
    embedder = HashingEmbedder(settings.embedding_dimension)
    store = InMemoryVectorStore()
    first = ingest(settings, storage, embedder, store, ingest_request(*ids, "knowledge/a/doc.txt", BODY.encode(), chunkCharacters=800, chunkOverlap=100))
    assert first.status == "READY"
    before = [(c.chunk_id, c.content) for c in store._chunks[ids[0]]]

    class BrokenEmbedder:
        model, dimension = embedder.model, embedder.dimension

        def embed(self, texts):
            raise ProviderError("down")

    reindex = ingest_request(*ids, "knowledge/a/doc.txt", BODY.encode(), chunkCharacters=2000, chunkOverlap=200)
    assert ingest(settings, storage, BrokenEmbedder(), store, reindex).status == FAILED

    class BrokenStore(InMemoryVectorStore):
        def replace_version(self, version_id, chunks):
            raise RuntimeError("database down")

    broken = BrokenStore()
    broken._chunks = store._chunks
    assert ingest(settings, storage, embedder, broken, reindex).status == FAILED

    # The object changed after verification: integrity failure, nothing written.
    put(settings, "knowledge/a/doc.txt", b"tampered")
    assert ingest(settings, storage, embedder, store, reindex).status == "INTEGRITY_FAILED"

    assert [(c.chunk_id, c.content) for c in store._chunks[ids[0]]] == before
