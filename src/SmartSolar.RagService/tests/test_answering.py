from app.answering import answer_question
from app.embeddings import HashingEmbedder
from app.llm import Completion, ExtractiveChatModel
from app.vectorstore import InMemoryVectorStore, StoredChunk

WARRANTY = "Tấm pin mặt trời được bảo hành sản phẩm 12 năm và bảo hành hiệu suất 25 năm."
PRICE = "Giá lắp đặt hệ 5 kWp tham khảo khoảng 60 triệu đồng."


class ScriptedModel:
    model = "scripted"

    def __init__(self, text):
        self.text = text
        self.calls = 0

    def complete(self, messages, max_tokens):
        self.calls += 1
        return Completion(self.text, 100, 10, self.model)


def setup(settings):
    embedder = HashingEmbedder(settings.embedding_dimension)
    store = InMemoryVectorStore()
    for version, document, text in (("v1", "d1", WARRANTY), ("v2", "d2", PRICE)):
        store.replace_version(version, [StoredChunk(f"{version}:0", version, document, 0, 1, None, text, "h", embedder.embed([text])[0], embedder.model)])
    return embedder, store


def allowed(*versions):
    return [{"versionId": v, "documentId": v.replace("v", "d"), "title": f"Doc {v}", "versionNumber": 1} for v in versions]


def test_grounded_answer_cites_the_allowed_source(settings):
    embedder, store = setup(settings)
    outcome = answer_question(settings, embedder, store, ExtractiveChatModel(), "Tấm pin bảo hành bao lâu?", [], allowed("v1", "v2"), 4, 2000, 300)
    assert outcome.grounded
    assert outcome.citations[0]["versionId"] == "v1"
    assert outcome.citations[0]["documentId"] == "d1"
    assert "[1]" in outcome.answer


def test_versions_outside_the_allow_list_are_never_retrieved(settings):
    embedder, store = setup(settings)
    model = ScriptedModel("Bảo hành 12 năm [1]")
    outcome = answer_question(settings, embedder, store, model, "Tấm pin bảo hành bao lâu?", [], allowed("v2"), 4, 2000, 300)
    assert all(c["versionId"] == "v2" for c in outcome.citations)
    empty = answer_question(settings, embedder, store, model, "Tấm pin bảo hành bao lâu?", [], [], 4, 2000, 300)
    assert not empty.grounded and empty.citations == []


def test_insufficient_sentinel_and_invalid_citations_are_not_grounded(settings):
    embedder, store = setup(settings)
    for text in ("INSUFFICIENT_CONTEXT", "Chắc là 30 năm [7]", "Không có trích dẫn", ""):
        outcome = answer_question(settings, embedder, store, ScriptedModel(text), "Tấm pin bảo hành bao lâu?", [], allowed("v1"), 4, 2000, 300)
        assert not outcome.grounded, text
        assert outcome.answer is None and outcome.citations == []


def test_bogus_markers_are_removed_from_a_grounded_answer(settings):
    embedder, store = setup(settings)
    outcome = answer_question(settings, embedder, store, ScriptedModel("12 năm [1] và [9]"), "bảo hành tấm pin", [], allowed("v1"), 4, 2000, 300)
    assert outcome.grounded
    assert "[9]" not in outcome.answer
    assert len(outcome.citations) == 1


def test_no_relevant_hits_skip_the_model(settings):
    settings.min_score = 0.99
    embedder, store = setup(settings)
    model = ScriptedModel("x [1]")
    outcome = answer_question(settings, embedder, store, model, "thời tiết hôm nay", [], allowed("v1", "v2"), 4, 2000, 300)
    assert not outcome.grounded
    assert model.calls == 0
