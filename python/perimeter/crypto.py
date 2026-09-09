from __future__ import annotations

import hashlib
import hmac


def sign_body_v1(secret: str, body: bytes) -> str:
    digest = hmac.new(secret.encode("utf-8"), body, hashlib.sha256).hexdigest()
    return "v1=" + digest
