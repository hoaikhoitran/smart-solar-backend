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

    def complete(self, messages, max_tokens, options=None):
        self.calls += 1
        self.options = options
        self.max_tokens = max_tokens
        self.system = messages[0]["content"]
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


SPEC = ("Tài liệu thử nghiệm SmartSolar: tấm pin TOPCon SS-PV-T580 có hiệu suất 22,5% và công suất 580 Wp. "
        "Dữ liệu giả lập, không phải thông số sản phẩm chính thức.")


def with_spec(settings):
    embedder, store = setup(settings)
    store.replace_version("v3", [StoredChunk("v3:0", "v3", "d3", 0, 1, None, SPEC, "h", embedder.embed([SPEC])[0], embedder.model)])
    return embedder, store


def test_a_complete_sentence_with_the_source_disclaimer_is_kept_verbatim_under_v2(settings):
    embedder, store = with_spec(settings)
    text = ("Theo tài liệu thử nghiệm SmartSolar, tấm pin TOPCon SS-PV-T580 có hiệu suất 22,5% [1]. "
            "Đây là dữ liệu giả lập dùng để kiểm thử, không phải thông số sản phẩm chính thức [1].")
    model = ScriptedModel(text)
    outcome = answer_question(settings, embedder, store, model, "Tấm pin TOPCon SS-PV-T580 có hiệu suất bao nhiêu?", [], allowed("v3"), 4, 2000, 300,
                              prompt_version="v2")
    assert outcome.grounded and outcome.answer == text
    assert [c["versionId"] for c in outcome.citations] == ["v3"]
    assert "ngắn gọn" not in model.system and "câu hoàn chỉnh" in model.system


def test_a_multi_part_answer_keeps_every_part_with_its_citations_and_an_unanswered_part_stays_grounded(settings):
    embedder, store = with_spec(settings)
    text = ("Theo tài liệu:\n"
            "- Hiệu suất của tấm pin TOPCon SS-PV-T580 là 22,5% [1].\n"
            "- Công suất của tấm pin này là 580 Wp [1].\n"
            "- Tấm pin được bảo hành sản phẩm 12 năm và bảo hành hiệu suất 25 năm [2].\n"
            "Tài liệu không đề cập giá bán của tấm pin này.")
    outcome = answer_question(settings, embedder, store, ScriptedModel(text), "SS-PV-T580: hiệu suất, công suất, bảo hành và giá?", [],
                              allowed("v3", "v1", "v2"), 4, 2000, 600, prompt_version="v2")
    assert outcome.grounded and outcome.answer == text          # bullets, line breaks and both markers survive
    assert len(outcome.citations) == 2 and all(c["versionId"] in {"v3", "v1", "v2"} for c in outcome.citations)
    # The sentinel is for "no source answers at all": inside a partial answer it still makes the whole answer ungrounded,
    # which is why v2 tells the model to describe a missing part in words instead.
    misuse = answer_question(settings, embedder, store, ScriptedModel("Hiệu suất 22,5% [1]. INSUFFICIENT_CONTEXT"), "hiệu suất và giá?", [],
                             allowed("v3"), 4, 2000, 600, prompt_version="v2")
    assert not misuse.grounded and misuse.answer is None


def test_the_answer_budget_is_the_smaller_of_the_request_and_the_environment_ceiling(settings):
    embedder, store = setup(settings)
    settings.max_output_tokens = 400
    model = ScriptedModel("12 năm [1]")
    answer_question(settings, embedder, store, model, "bảo hành tấm pin", [], allowed("v1"), 4, 2000, 900)
    assert model.max_tokens == 400
    answer_question(settings, embedder, store, model, "bảo hành tấm pin", [], allowed("v1"), 4, 2000, 250)
    assert model.max_tokens == 250
