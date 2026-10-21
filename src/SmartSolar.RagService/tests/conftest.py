import hashlib
import io
import os
import sys
import uuid

import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from app.config import Settings  # noqa: E402

SECRET = "test-shared-secret-0123456789abcdef"


def make_pdf(pages):
    """Minimal valid PDF with one Helvetica text line per page (None = blank page)."""
    objects = ["<< /Type /Catalog /Pages 2 0 R >>", None, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"]
    kids = []
    for text in pages:
        content = "" if text is None else f"BT /F1 12 Tf 72 720 Td ({text}) Tj ET"
        objects.append(f"<< /Length {len(content)} >>\nstream\n{content}\nendstream")
        content_id = len(objects)
        objects.append(f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {content_id} 0 R >>")
        kids.append(f"{len(objects)} 0 R")
    objects[1] = f"<< /Type /Pages /Kids [{' '.join(kids)}] /Count {len(kids)} >>"
    out = io.BytesIO()
    out.write(b"%PDF-1.4\n")
    offsets = []
    for number, body in enumerate(objects, start=1):
        offsets.append(out.tell())
        out.write(f"{number} 0 obj\n{body}\nendobj\n".encode("latin-1"))
    xref = out.tell()
    out.write(f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode())
    for offset in offsets:
        out.write(f"{offset:010d} 00000 n \n".encode())
    out.write(f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode())
    return out.getvalue()


def sha(data):
    return hashlib.sha256(data).hexdigest()


@pytest.fixture
def settings(tmp_path):
    return Settings(
        shared_secret=SECRET,
        vector_store="memory",
        storage="local",
        local_storage_dir=str(tmp_path),
        embedding_provider="hashing",
        embedding_dimension=256,
        llm_provider="extractive",
        min_score=0.05,
        chunk_characters=400,
        chunk_overlap=60,
    )


@pytest.fixture
def ids():
    return str(uuid.uuid4()), str(uuid.uuid4())
