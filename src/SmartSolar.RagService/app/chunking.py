"""Splits extracted sections into overlapping chunks that keep page and heading metadata."""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import List, Optional

from .extraction import Section


@dataclass
class Chunk:
    index: int
    text: str
    page: Optional[int]
    section: Optional[str]


_SENTENCE_END = re.compile(r"(?<=[.!?…])\s+")


def _pieces(text: str, size: int) -> List[str]:
    """Paragraphs, then sentences, then hard cuts, so no piece exceeds size."""
    out: List[str] = []
    for paragraph in [p.strip() for p in text.split("\n\n") if p.strip()]:
        if len(paragraph) <= size:
            out.append(paragraph)
            continue
        for sentence in _SENTENCE_END.split(paragraph):
            while len(sentence) > size:
                out.append(sentence[:size])
                sentence = sentence[size:]
            if sentence.strip():
                out.append(sentence.strip())
    return out


def chunk_sections(sections: List[Section], size: int, overlap: int) -> List[Chunk]:
    chunks: List[Chunk] = []
    for section in sections:
        current = ""
        for piece in _pieces(section.text, size):
            if current and len(current) + 2 + len(piece) > size:
                chunks.append(Chunk(len(chunks), current, section.page, section.heading))
                tail = current[-overlap:] if overlap > 0 else ""
                # Start the overlap at a word boundary.
                tail = tail[tail.find(" ") + 1:] if " " in tail else tail
                current = (tail + " " + piece).strip() if tail else piece
                if len(current) > size:
                    current = piece
            else:
                current = f"{current}\n\n{piece}" if current else piece
        if current.strip():
            chunks.append(Chunk(len(chunks), current, section.page, section.heading))
    return chunks
