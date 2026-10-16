import os

import pytest

from app.config import PDF, TEXT
from app.embeddings import HashingEmbedder, ProviderError
from app.ingestion import FAILED, ingest
from app.storage import LocalStorage, StorageError
from app.vectorstore import InMemoryVectorStore
from tests.conftest import make_pdf, sha

BODY = ("Hệ thống điện mặt trời áp mái giúp giảm hóa đơn tiền điện. " * 20 + "\n\n") * 4


def put(settings, key, data, bucket="kb"):
    path = os.path.join(settings.local_storage_dir, bucket, key)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(data)


def request(ids, key, data, content_type=TEXT):
    version, document = ids
    return {"versionId": version, "documentId": document, "bucket": "kb", "objectKey": key, "contentType": content_type,
            "expectedSha256": sha(data), "title": "T", "versionNumber": 1}


def test_ingestion_is_idempotent_and_replaces_chunks(settings, ids):
    put(settings, "knowledge/a/doc.txt", BODY.encode())
    store = InMemoryVectorStore()
    embedder = HashingEmbedder(settings.embedding_dimension)
    first = ingest(settings, LocalStorage(settings.local_storage_dir), embedder, store, request(ids, "knowledge/a/doc.txt", BODY.encode()))
    assert first.status == "READY" and first.chunk_count > 1
    second = ingest(settings, LocalStorage(settings.local_storage_dir), embedder, store, request(ids, "knowledge/a/doc.txt", BODY.encode()))
    assert second.chunk_count == first.chunk_count
    assert store.count(ids[0]) == first.chunk_count


def test_changed_object_is_never_indexed(settings, ids):
    put(settings, "knowledge/a/doc.txt", BODY.encode())
    req = request(ids, "knowledge/a/doc.txt", b"what the API verified")
    store = InMemoryVectorStore()
    outcome = ingest(settings, LocalStorage(settings.local_storage_dir), HashingEmbedder(64), store, req)
    assert outcome.status == "INTEGRITY_FAILED"
    assert store.count(ids[0]) == 0


def test_scanned_pdf_reports_ocr_required(settings, ids):
    data = make_pdf([None, None])
    put(settings, "knowledge/a/scan.pdf", data)
    outcome = ingest(settings, LocalStorage(settings.local_storage_dir), HashingEmbedder(64), InMemoryVectorStore(),
                     request(ids, "knowledge/a/scan.pdf", data, PDF))
    assert outcome.status == "OCR_REQUIRED"


def test_transient_failures_are_reported_as_failed(settings, ids):
    class BrokenEmbedder:
        model, dimension = "x", 8

        def embed(self, texts):
            raise ProviderError("down")

    class BrokenStorage:
        def read(self, *args):
            raise StorageError("down")

    put(settings, "knowledge/a/doc.txt", BODY.encode())
    req = request(ids, "knowledge/a/doc.txt", BODY.encode())
    assert ingest(settings, BrokenStorage(), HashingEmbedder(64), InMemoryVectorStore(), req).status == FAILED
    assert ingest(settings, LocalStorage(settings.local_storage_dir), BrokenEmbedder(), InMemoryVectorStore(), req).status == FAILED


def test_unsupported_types_and_oversized_objects(settings, ids):
    put(settings, "knowledge/a/doc.txt", BODY.encode())
    req = request(ids, "knowledge/a/doc.txt", BODY.encode(), "image/png")
    assert ingest(settings, LocalStorage(settings.local_storage_dir), HashingEmbedder(64), InMemoryVectorStore(), req).status == "UNSUPPORTED_CONTENT"
    settings.max_document_bytes = 10
    req = request(ids, "knowledge/a/doc.txt", BODY.encode())
    assert ingest(settings, LocalStorage(settings.local_storage_dir), HashingEmbedder(64), InMemoryVectorStore(), req).status == "UNSUPPORTED_CONTENT"


@pytest.mark.parametrize("key", ["../etc/passwd", "/abs/path", "a/../../b", "a\\b"])
def test_local_storage_rejects_path_traversal(settings, key):
    with pytest.raises(StorageError):
        LocalStorage(settings.local_storage_dir).read("kb", key, None, 1000)


def test_ingestion_stops_at_its_deadline_and_reports_a_retryable_failure(settings, ids):
    put(settings, "knowledge/a/doc.txt", BODY.encode())
    store = InMemoryVectorStore()
    calls = {"n": 0}

    def clock():
        # Each check is one "minute" later: the deadline passes while embedding batches.
        calls["n"] += 1
        return calls["n"] * 60.0

    settings.ingest_deadline_seconds = 150
    settings.embedding_batch_size = 1
    outcome = ingest(settings, LocalStorage(settings.local_storage_dir), HashingEmbedder(64), store,
                     request(ids, "knowledge/a/doc.txt", BODY.encode()), clock=clock)
    assert outcome.status == FAILED
    assert "deadline" in outcome.error
    assert store.count(ids[0]) == 0


def test_deadline_must_stay_below_the_api_timeout(settings):
    settings.ingest_deadline_seconds = 5
    with pytest.raises(ValueError, match="RAG_INGEST_DEADLINE_SECONDS"):
        settings.validate()
