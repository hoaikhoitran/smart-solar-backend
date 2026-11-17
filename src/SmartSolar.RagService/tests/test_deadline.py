"""The caller's deadline (``deadlineSeconds``): a request the API no longer waits for never takes the generation slot or calls the model,
the slot wait never outlasts the caller, and the model call is cut at the remaining time (2026-11-17 latency/reliability fix)."""

import json
import os
import time
import uuid

from fastapi.testclient import TestClient

from app.answering import MIN_GENERATION_SECONDS
from app.llm import Completion
from app.main import build_services, create_app
from app.security import sign
from app.vectorstore import InMemoryVectorStore
from tests.conftest import SECRET, sha

TEXT = "Tấm pin mặt trời được bảo hành sản phẩm 12 năm. Inverter được bảo hành 5 năm."


class TimedModel:
    """Records the per-call timeout; optionally sleeps to occupy the generation slot."""

    model = "timed-test"

    def __init__(self, sleep_seconds: float = 0.0):
        self.sleep_seconds = sleep_seconds
        self.timeouts = []

    def complete(self, messages, max_tokens, options=None, timeout_seconds=None):
        self.timeouts.append(timeout_seconds)
        if self.sleep_seconds:
            time.sleep(self.sleep_seconds)
        return Completion("Tấm pin được bảo hành 12 năm [1].", 50, 10, self.model)


def post(client, path, payload):
    body = json.dumps(payload).encode()
    ts = str(int(time.time()))
    return client.post(path, content=body, headers={"Content-Type": "application/json", "X-SmartSolar-Timestamp": ts,
                                                    "X-SmartSolar-Signature": sign(SECRET, ts, body)})


def ingested_client(settings, model):
    services = build_services(settings, store=InMemoryVectorStore(), model=model)
    client = TestClient(create_app(services))
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    key = f"knowledge/{uuid.uuid4()}/warranty.txt"
    path = os.path.join(settings.local_storage_dir, "kb", key)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(TEXT.encode())
    assert post(client, "/internal/v1/ingest", {
        "versionId": version, "documentId": document, "bucket": "kb", "objectKey": key, "contentType": "text/plain",
        "expectedSha256": sha(TEXT.encode()), "title": "Bảo hành", "versionNumber": 1,
    }).status_code == 200
    allowed = [{"versionId": version, "documentId": document, "title": "Bảo hành", "versionNumber": 1}]
    return services, client, allowed


def ask(client, allowed, **extra):
    return post(client, "/internal/v1/answer", {"question": "Tấm pin bảo hành bao lâu?", "allowedVersions": allowed, "topK": 4,
                                               "maxContextTokens": 2000, "maxOutputTokens": 300, **extra})


def test_without_a_deadline_the_answer_is_unchanged_and_the_provider_default_timeout_applies(settings):
    model = TimedModel()
    _, client, allowed = ingested_client(settings, model)
    answer = ask(client, allowed)
    assert answer.status_code == 200, answer.text
    assert answer.json()["grounded"] is True
    assert model.timeouts == [None]


def test_the_model_call_is_cut_at_the_remaining_deadline(settings):
    model = TimedModel()
    _, client, allowed = ingested_client(settings, model)
    answer = ask(client, allowed, deadlineSeconds=30)
    assert answer.status_code == 200, answer.text
    assert len(model.timeouts) == 1 and 25 < model.timeouts[0] <= 30


def test_a_request_whose_deadline_is_too_close_never_calls_the_model(settings):
    model = TimedModel()
    _, client, allowed = ingested_client(settings, model)
    answer = ask(client, allowed, deadlineSeconds=MIN_GENERATION_SECONDS / 2)
    assert answer.status_code == 503
    assert answer.json()["detail"] == "deadline exceeded"
    assert model.timeouts == []


def test_waiting_for_the_slot_never_outlasts_the_caller_and_the_abandoned_request_does_not_generate(settings):
    settings.max_concurrent_generations = 1
    settings.queue_wait_seconds = 30   # the environment wait no longer applies when the caller sends its deadline
    model = TimedModel()
    services, client, allowed = ingested_client(settings, model)
    services.generations.acquire()   # another generation holds the only slot
    try:
        started = time.monotonic()
        answer = ask(client, allowed, deadlineSeconds=3.5)
        elapsed = time.monotonic() - started
    finally:
        services.generations.release()
    assert answer.status_code == 503
    assert answer.json()["detail"] == "busy"
    assert elapsed < 4, f"waited {elapsed:.1f}s although the caller only waits 3.5s (queue wait 30s)"
    assert model.timeouts == []
    # The slot is free again and the next request is served.
    assert ask(client, allowed, deadlineSeconds=30).status_code == 200


def test_the_deadline_field_is_validated(settings):
    _, client, allowed = ingested_client(settings, TimedModel())
    assert ask(client, allowed, deadlineSeconds=0).status_code in (400, 422)
    assert ask(client, allowed, deadlineSeconds=5000).status_code in (400, 422)


class SlowProviderModel:
    """The provider call times out (as httpx does when the per-call timeout is reached)."""

    model = "slow-test"

    def complete(self, messages, max_tokens, options=None, timeout_seconds=None):
        from app.embeddings import ProviderTimeout
        raise ProviderTimeout("LLM provider timed out")


def test_a_model_call_cut_at_the_deadline_is_reported_as_deadline_exceeded_not_as_an_outage(settings):
    _, client, allowed = ingested_client(settings, SlowProviderModel())
    with_deadline = ask(client, allowed, deadlineSeconds=30)
    assert with_deadline.status_code == 503
    assert with_deadline.json()["detail"] == "deadline exceeded"
    # Without a deadline (older API) a provider timeout stays a provider failure, as before.
    without = ask(client, allowed)
    assert without.status_code == 503
    assert without.json()["detail"] == "provider unavailable"


def test_with_a_deadline_a_request_waits_for_the_slot_beyond_the_environment_queue_wait_and_is_answered(settings):
    import threading
    settings.max_concurrent_generations = 1
    settings.queue_wait_seconds = 1   # the old fixed wait: this request would have been refused as "busy"
    model = TimedModel()
    services, client, allowed = ingested_client(settings, model)
    services.generations.acquire()
    releaser = threading.Timer(3.0, services.generations.release)   # the other generation finishes after 3 s
    releaser.start()
    started = time.monotonic()
    answer = ask(client, allowed, deadlineSeconds=20)
    elapsed = time.monotonic() - started
    releaser.join()
    assert answer.status_code == 200, answer.text
    assert 2.5 < elapsed < 10
    # Without a deadline (older callers) the environment wait still applies.
    services.generations.acquire()
    try:
        assert ask(client, allowed).json()["detail"] == "busy"
    finally:
        services.generations.release()
