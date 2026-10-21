"""Runs only when RAG_TEST_DATABASE_URL points at a disposable LOCAL PostgreSQL with pgvector.

A throwaway database is created, the review SQL (sql/knowledge_chunk.sql) is applied to it on top of
minimal stand-ins for the two referenced API tables, and the database is dropped afterwards.
"""

import os
import uuid
from urllib.parse import urlparse

import pytest

from app.embeddings import HashingEmbedder
from app.vectorstore import PgVectorStore, StoredChunk

URL = os.environ.get("RAG_TEST_DATABASE_URL")
pytestmark = pytest.mark.skipif(not URL, reason="Set RAG_TEST_DATABASE_URL to a disposable local PostgreSQL with pgvector")


@pytest.fixture
def database():
    import psycopg

    parsed = urlparse(URL)
    assert parsed.hostname in ("localhost", "127.0.0.1"), "refusing to run against a non-local database"
    name = f"rag_test_{uuid.uuid4().hex}"
    with psycopg.connect(URL, autocommit=True) as admin:
        admin.execute(f'CREATE DATABASE "{name}"')
    url = parsed._replace(path=f"/{name}").geturl()
    with psycopg.connect(url, autocommit=True) as conn:
        conn.execute("CREATE TABLE knowledge_document (id uuid PRIMARY KEY)")
        conn.execute("CREATE TABLE knowledge_document_version (id uuid PRIMARY KEY)")
        with open(os.path.join(os.path.dirname(__file__), "..", "sql", "knowledge_chunk.sql"), encoding="utf-8") as handle:
            conn.execute(handle.read())
    yield url
    with psycopg.connect(URL, autocommit=True) as admin:
        admin.execute(f'DROP DATABASE IF EXISTS "{name}" WITH (FORCE)')


def chunks(embedder, version, document, texts):
    return [StoredChunk(f"{version.replace('-', '')}:{i}", version, document, i, 1, None, t, "0" * 64, v, embedder.model)
            for i, (t, v) in enumerate(zip(texts, embedder.embed(texts)))]


def test_replace_search_filter_and_delete(database):
    import psycopg

    embedder = HashingEmbedder(1536)
    v1, v2, d1, d2 = (str(uuid.uuid4()) for _ in range(4))
    with psycopg.connect(database, autocommit=True) as conn:
        for table, ids in (("knowledge_document", (d1, d2)), ("knowledge_document_version", (v1, v2))):
            for i in ids:
                conn.execute(f"INSERT INTO {table} (id) VALUES (%s)", (i,))  # noqa: S608 - fixed table names
    store = PgVectorStore(database)
    assert store.ready()
    store.replace_version(v1, chunks(embedder, v1, d1, ["bảo hành tấm pin 12 năm", "inverter bảo hành 5 năm"]))
    store.replace_version(v1, chunks(embedder, v1, d1, ["bảo hành tấm pin 12 năm", "inverter bảo hành 5 năm"]))  # idempotent
    store.replace_version(v2, chunks(embedder, v2, d2, ["giá hệ 5 kWp khoảng 60 triệu"]))

    query = embedder.embed(["tấm pin bảo hành bao lâu"])[0]
    hits = store.search(query, [v1, v2], 5, embedder.model)
    assert len(hits) == 3
    assert hits[0].version_id == v1 and "tấm pin" in hits[0].content
    # The allow-list is enforced by the query itself.
    assert {h.version_id for h in store.search(query, [v2], 5, embedder.model)} == {v2}
    assert store.search(query, [], 5, embedder.model) == []
    assert store.delete_version(v1) == 2
    assert {h.version_id for h in store.search(query, [v1, v2], 5, embedder.model)} == {v2}


def test_concurrent_ingests_of_one_version_never_duplicate_or_fail(database):
    import threading

    import psycopg

    embedder = HashingEmbedder(1536)
    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    with psycopg.connect(database, autocommit=True) as conn:
        conn.execute("INSERT INTO knowledge_document (id) VALUES (%s)", (document,))
        conn.execute("INSERT INTO knowledge_document_version (id) VALUES (%s)", (version,))
    store = PgVectorStore(database)
    texts = [f"đoạn {i} về bảo hành tấm pin" for i in range(30)]
    errors = []

    def run():
        try:
            store.replace_version(version, chunks(embedder, version, document, texts))
        except Exception as exc:  # noqa: BLE001 - collected for the assertion
            errors.append(exc)

    threads = [threading.Thread(target=run) for _ in range(4)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()

    assert errors == []
    with psycopg.connect(database) as conn:
        count = conn.execute("SELECT count(*) FROM knowledge_chunk WHERE version_id = %s", (version,)).fetchone()[0]
    assert count == 30



def _unit(*weights):
    vector = [0.0] * 1536
    for i, w in enumerate(weights):
        vector[i] = w
    return vector


def _seed_version(database):
    import psycopg

    version, document = str(uuid.uuid4()), str(uuid.uuid4())
    with psycopg.connect(database, autocommit=True) as conn:
        conn.execute("INSERT INTO knowledge_document (id) VALUES (%s)", (document,))
        conn.execute("INSERT INTO knowledge_document_version (id) VALUES (%s)", (version,))
    return version, document


def test_1536_dimension_vectors_are_stored_and_ranked_by_cosine_similarity(database):
    store = PgVectorStore(database, 1536)
    version, document = _seed_version(database)
    vectors = {"same": _unit(1.0), "diagonal": _unit(1.0, 1.0), "orthogonal": _unit(0.0, 1.0)}
    store.replace_version(version, [
        StoredChunk(f"{version}:{i}", version, document, i, 1, None, name, "0" * 64, vec, "text-embedding-3-small")
        for i, (name, vec) in enumerate(vectors.items())])

    hits = store.search(_unit(2.0), [version], 3, "text-embedding-3-small")   # magnitude does not matter for cosine

    assert [h.content for h in hits] == ["same", "diagonal", "orthogonal"]
    assert hits[0].score == pytest.approx(1.0, abs=1e-6)
    assert hits[1].score == pytest.approx(0.70710678, abs=1e-6)
    assert hits[2].score == pytest.approx(0.0, abs=1e-6)
    assert store.search(_unit(2.0), [version], 3, "hashing-dev") == []         # other model: never mixed


def test_wrong_dimensions_are_refused_by_the_store_and_by_the_column(database):
    import psycopg

    from app.vectorstore import EmbeddingDimensionError

    store = PgVectorStore(database, 1536)
    version, document = _seed_version(database)
    with pytest.raises(EmbeddingDimensionError):
        store.replace_version(version, [StoredChunk(f"{version}:0", version, document, 0, 1, None, "x", "0" * 64, [0.1] * 768, "m")])
    with pytest.raises(psycopg.errors.DataException, match="expected 1536 dimensions"), psycopg.connect(database) as conn:
        conn.execute("INSERT INTO knowledge_chunk (chunk_id, version_id, document_id, chunk_index, content, content_sha256, embedding, embedding_model)"
                     " VALUES ('c', %s, %s, 0, 'x', repeat('0', 64), '[1,2,3]'::vector, 'm')", (version, document))


def test_readiness_checks_the_column_dimension(database):
    assert PgVectorStore(database, 1536).schema_dimension() == 1536
    assert PgVectorStore(database, 1536).ready()
    assert not PgVectorStore(database, 768).ready()


def test_the_schema_has_one_cosine_hnsw_index_and_no_duplicate_indexes(database):
    import psycopg

    with psycopg.connect(database) as conn:
        indexes = dict(conn.execute("SELECT indexname, indexdef FROM pg_indexes WHERE tablename = 'knowledge_chunk'").fetchall())
        extension = conn.execute("SELECT extversion FROM pg_extension WHERE extname = 'vector'").fetchone()[0]
    assert sorted(indexes) == ["ix_knowledge_chunk_embedding_hnsw", "knowledge_chunk_pkey", "ux_knowledge_chunk_version_index"]
    assert "USING hnsw (embedding vector_cosine_ops)" in indexes["ix_knowledge_chunk_embedding_hnsw"]
    major, minor = (int(x) for x in extension.split(".")[:2])
    assert (major, minor) >= (0, 5), f"pgvector {extension} has no HNSW"
