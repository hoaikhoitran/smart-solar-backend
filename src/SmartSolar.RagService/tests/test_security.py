import hashlib
import hmac

from app.security import SignatureVerifier, sign
from tests.conftest import SECRET


def test_signature_matches_the_dotnet_formula():
    # Same vector as KnowledgeBaseTests.Rag_requests_are_signed_with_hmac_over_timestamp_and_body.
    expected = hmac.new(b"secret", b'1700000000.{"a":1}', hashlib.sha256).hexdigest()
    assert sign("secret", "1700000000", b'{"a":1}') == expected


def test_valid_signature_is_accepted_once():
    verifier = SignatureVerifier(SECRET, 300)
    body = b'{"x":1}'
    signature = sign(SECRET, "1000", body)
    assert verifier.verify("1000", signature, body, now=1100) is None
    assert verifier.verify("1000", signature, body, now=1100) == "replayed"


def test_stale_tampered_missing_and_wrong_key_requests_are_rejected():
    verifier = SignatureVerifier(SECRET, 300)
    body = b'{"x":1}'
    assert verifier.verify("1000", sign(SECRET, "1000", body), body, now=1301) == "stale_timestamp"
    assert verifier.verify("2000", sign(SECRET, "2000", body), b'{"x":2}', now=2000) == "bad_signature"
    assert verifier.verify("2000", sign("another-secret-of-sufficient-length!!", "2000", body), body, now=2000) == "bad_signature"
    assert verifier.verify(None, "abc", body, now=2000) == "missing_signature"
    assert verifier.verify("20x0", "abc", body, now=2000) == "bad_timestamp"
