"""HMAC authentication of calls from the SmartSolar API.

signature = hex(HMAC-SHA256(secret, f"{timestamp}.{raw_body}")), sent with X-SmartSolar-Timestamp
(unix seconds). Requests outside the time window are rejected, and a signature is accepted once
(process-local replay cache; the time window bounds replay across restarts or replicas).
"""

from __future__ import annotations

import hashlib
import hmac
import threading
import time
from typing import Dict, Optional

TIMESTAMP_HEADER = "X-SmartSolar-Timestamp"
SIGNATURE_HEADER = "X-SmartSolar-Signature"


def sign(secret: str, timestamp: str, body: bytes) -> str:
    return hmac.new(secret.encode("utf-8"), timestamp.encode("ascii") + b"." + body, hashlib.sha256).hexdigest()


class ReplayCache:
    def __init__(self, window_seconds: int) -> None:
        self._window = window_seconds
        self._seen: Dict[str, float] = {}
        self._lock = threading.Lock()

    def first_use(self, signature: str, now: float) -> bool:
        with self._lock:
            if len(self._seen) > 10_000:
                self._seen = {k: v for k, v in self._seen.items() if v > now - 2 * self._window}
            if signature in self._seen:
                return False
            self._seen[signature] = now
            return True


class SignatureVerifier:
    def __init__(self, secret: str, window_seconds: int = 300) -> None:
        self._secret = secret
        self._window = window_seconds
        self._replay = ReplayCache(window_seconds)

    def verify(self, timestamp: Optional[str], signature: Optional[str], body: bytes, now: Optional[float] = None) -> Optional[str]:
        """Returns None when valid, otherwise a short reason code (never secret material)."""
        now = time.time() if now is None else now
        if not timestamp or not signature:
            return "missing_signature"
        if not timestamp.isdigit() or len(timestamp) > 12:
            return "bad_timestamp"
        if abs(now - int(timestamp)) > self._window:
            return "stale_timestamp"
        expected = sign(self._secret, timestamp, body)
        if not hmac.compare_digest(expected, signature.strip().lower()):
            return "bad_signature"
        if not self._replay.first_use(expected, now):
            return "replayed"
        return None
