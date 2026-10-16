from app.chunking import chunk_sections
from app.extraction import Section


def test_chunks_respect_size_keep_metadata_and_overlap():
    text = "\n\n".join(f"Đoạn {i}. " + "Nội dung về điện mặt trời áp mái. " * 8 for i in range(12))
    chunks = chunk_sections([Section(text, page=3, heading="Tổng quan")], size=400, overlap=60)
    assert len(chunks) > 3
    assert all(len(c.text) <= 400 for c in chunks)
    assert all(c.page == 3 and c.section == "Tổng quan" for c in chunks)
    assert [c.index for c in chunks] == list(range(len(chunks)))
    # Consecutive chunks share overlapping text.
    assert chunks[0].text[-30:].split()[-1] in chunks[1].text


def test_very_long_unbroken_text_is_hard_split():
    chunks = chunk_sections([Section("x" * 1000)], size=300, overlap=50)
    assert all(len(c.text) <= 300 for c in chunks)
    assert sum(len(c.text) for c in chunks) >= 1000
