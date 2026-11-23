import json
import os
import time
import uuid

import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.main import build_services, create_app
from app.security import sign
from app.vectorstore import InMemoryVectorStore
from tests.conftest import SECRET, sha

TEXT = ("Tấm pin mặt trời được bảo hành sản phẩm 12 năm và bảo hành hiệu suất 25 năm. " * 6 + "\n\n") * 3


@pytest.fixture
def client(settings):
    services = build_services(settings, store=InMemoryVectorStore())
    return TestClient(create_app(services))


def post(client, path, payload, secret=SECRET, timestamp=None):
    body = json.dumps(payload).encode()
    ts = str(int(time.time()) if timestamp is None else timestamp)
    return client.post(path, content=body, headers={"Content-Type": "application/json", "X-SmartSolar-Timestamp": ts,
                                                    "X-SmartSolar-Signature": sign(secret, ts, body)})


def test_health_endpoints(client):
    assert client.get("/health/live").json() == {"status": "ok"}
    assert client.get("/health/ready").status_code == 200
    assert client.get("/docs").status_code == 404
    assert client.get("/openapi.json").status_code == 404


def test_internal_endpoints_require_a_valid_fresh_signature(client):
    payload = {"versionId": str(uuid.uuid4())}
    assert client.post("/internal/v1/delete", json=payload).status_code == 401
    assert post(client, "/internal/v1/delete", payload, secret="wrong-secret-wrong-secret-wrong-secret").status_code == 401
    assert post(client, "/internal/v1/delete", payload, timestamp=int(time.time()) - 3600).status_code == 401
    assert post(client, "/internal/v1/delete", payload).status_code == 200


def test_oversized_and_malformed_requests_are_rejected(client):
    big = {"question": "x" * 300_000, "allowedVersions": []}
    assert post(client, "/internal/v1/answer", big).status_code == 413
    assert post(client, "/internal/v1/answer", {"question": ""}).status_code == 422
    assert post(client, "/internal/v1/answer", {"question": "q", "topK": 1000}).status_code == 422


def test_ingest_answer_delete_round_trip(client, settings):
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    key = f"knowledge/{uuid.uuid4()}/warranty.txt"
    path = os.path.join(settings.local_storage_dir, "kb", key)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(TEXT.encode())

    ingested = post(client, "/internal/v1/ingest", {
        "versionId": version, "documentId": document, "bucket": "kb", "objectKey": key, "contentType": "text/plain",
        "expectedSha256": sha(TEXT.encode()), "title": "Bảo hành", "versionNumber": 1,
    })
    assert ingested.status_code == 200, ingested.text
    assert ingested.json()["status"] == "READY"

    allowed = [{"versionId": version, "documentId": document, "title": "Bảo hành", "versionNumber": 1}]
    answer = post(client, "/internal/v1/answer", {"question": "Tấm pin bảo hành bao lâu?", "history": [], "allowedVersions": allowed,
                                                  "topK": 4, "maxContextTokens": 2000, "maxOutputTokens": 300}).json()
    assert answer["grounded"] is True
    assert answer["citations"][0]["versionId"] == version
    assert answer["usage"]["promptTokens"] > 0
    assert answer["insufficientReason"] is None and answer["usableChunks"] >= 1

    not_allowed = post(client, "/internal/v1/answer", {"question": "Tấm pin bảo hành bao lâu?", "allowedVersions": []}).json()
    assert not_allowed["grounded"] is False and not_allowed["citations"] == []
    # Safe diagnostics only: a reason code and counts, never chunk content.
    assert (not_allowed["insufficientReason"], not_allowed["retrievedChunks"]) == ("NO_INDEXED_CHUNKS", 0)

    deleted = post(client, "/internal/v1/delete", {"versionId": version}).json()
    assert deleted["deleted"] >= 1
    after = post(client, "/internal/v1/answer", {"question": "Tấm pin bảo hành bao lâu?", "allowedVersions": allowed}).json()
    assert after["grounded"] is False
    assert after["insufficientReason"] == "NO_INDEXED_CHUNKS"   # vectors deleted: a reindex would be required


def test_configuration_is_validated_without_echoing_secrets():
    with pytest.raises(ValueError) as weak:
        Settings(shared_secret="short", storage="local").validate()
    assert "short" not in str(weak.value)
    with pytest.raises(ValueError, match="production"):
        Settings(shared_secret=SECRET, environment="production", storage="local").validate()
    with pytest.raises(ValueError, match="RAG_LLM_BASE_URL"):
        Settings(shared_secret=SECRET, storage="local", llm_provider="openai").validate()
    Settings(shared_secret=SECRET, storage="local").validate()


def test_identical_calls_with_their_own_request_id_are_accepted_but_a_byte_for_byte_replay_is_not(client):
    # Two users asking the same question in the same second: same content, different requestId -> both answered.
    question = {"question": "Tấm pin bảo hành bao lâu?", "allowedVersions": [], "topK": 4, "maxContextTokens": 2000, "maxOutputTokens": 300}
    ts = str(int(time.time()))
    first = post(client, "/internal/v1/answer", {**question, "requestId": uuid.uuid4().hex}, timestamp=ts)
    second = post(client, "/internal/v1/answer", {**question, "requestId": uuid.uuid4().hex}, timestamp=ts)
    assert (first.status_code, second.status_code) == (200, 200)
    # The exact same signed bytes again: still refused as a replay.
    body = json.dumps({**question, "requestId": "fixed-id"}).encode()
    headers = {"Content-Type": "application/json", "X-SmartSolar-Timestamp": ts, "X-SmartSolar-Signature": sign(SECRET, ts, body)}
    assert client.post("/internal/v1/answer", content=body, headers=headers).status_code == 200
    assert client.post("/internal/v1/answer", content=body, headers=headers).status_code == 401
    # Without a requestId (older API) identical calls in one second still collide: the old behaviour, kept for compatibility.
    assert post(client, "/internal/v1/answer", question, timestamp=ts).status_code == 200
    assert post(client, "/internal/v1/answer", question, timestamp=ts).status_code == 401
    # A malformed requestId is a validation error, never silently ignored.
    assert post(client, "/internal/v1/answer", {**question, "requestId": "bad id!"}).status_code == 422
