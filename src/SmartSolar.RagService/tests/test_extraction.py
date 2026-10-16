import io

import docx

from app.config import DOCX, MARKDOWN, PDF, TEXT
from app.extraction import OCR_REQUIRED, READY, UNSUPPORTED_CONTENT, extract
from tests.conftest import make_pdf

LIMITS = dict(max_pages=50, max_characters=100_000, max_uncompressed_bytes=10_000_000)
LONG = "Solar panel warranty is twelve years for product defects and twenty five years for output. " * 2


def test_pdf_text_is_extracted_with_page_numbers():
    result = extract(make_pdf([LONG, LONG]), PDF, **LIMITS)
    assert result.status == READY
    assert result.page_count == 2
    assert [s.page for s in result.sections] == [1, 2]
    assert "twelve years" in result.sections[0].text


def test_scanned_pdf_without_text_requires_ocr():
    result = extract(make_pdf([None, None, None]), PDF, **LIMITS)
    assert result.status == OCR_REQUIRED
    assert result.page_count == 3


def test_page_limit_encryption_and_garbage_are_rejected():
    assert extract(make_pdf([LONG] * 3), PDF, max_pages=2, max_characters=100_000, max_uncompressed_bytes=1).status == UNSUPPORTED_CONTENT
    assert extract(b"%PDF-1.4 garbage", PDF, **LIMITS).status == UNSUPPORTED_CONTENT
    assert extract(b"not a pdf at all", PDF, **LIMITS).status == UNSUPPORTED_CONTENT
    assert extract(b"x", "image/png", **LIMITS).status == UNSUPPORTED_CONTENT


def _docx_bytes():
    document = docx.Document()
    document.add_heading("Bảo hành", level=1)
    document.add_paragraph("Tấm pin được bảo hành sản phẩm 12 năm và hiệu suất 25 năm.")
    document.add_heading("Lắp đặt", level=1)
    document.add_paragraph("Đội lắp đặt hoàn thành trong 2 đến 3 ngày làm việc.")
    table = document.add_table(rows=1, cols=2)
    table.rows[0].cells[0].text = "Inverter"
    table.rows[0].cells[1].text = "5 năm"
    buffer = io.BytesIO()
    document.save(buffer)
    return buffer.getvalue()


def test_docx_sections_keep_headings_and_tables():
    result = extract(_docx_bytes(), DOCX, **LIMITS)
    assert result.status == READY
    headings = [s.heading for s in result.sections]
    assert "Bảo hành" in headings and "Lắp đặt" in headings
    assert any("Inverter | 5 năm" in s.text for s in result.sections)


def test_docx_that_expands_beyond_the_limit_is_rejected():
    result = extract(_docx_bytes(), DOCX, max_pages=50, max_characters=100_000, max_uncompressed_bytes=1000)
    assert result.status == UNSUPPORTED_CONTENT
    assert "size limit" in result.error


def test_text_and_markdown():
    assert extract(LONG.encode(), TEXT, **LIMITS).status == READY
    md = extract(("# Giá\n" + LONG + "\n## Bảo hành\n" + LONG).encode(), MARKDOWN, **LIMITS)
    assert [s.heading for s in md.sections] == ["Giá", "Bảo hành"]
    assert extract(b"\xff\xfe\x00bad", TEXT, **LIMITS).status == UNSUPPORTED_CONTENT
    assert extract(b"short", TEXT, **LIMITS).status == UNSUPPORTED_CONTENT
