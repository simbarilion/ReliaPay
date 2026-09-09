from __future__ import annotations

import json


def provider_payment_bytes(external_request_id: str, amount: str, currency: str) -> bytes:
    payload = {
        "operationId": external_request_id,
        "amount": amount,
        "currency": currency,
    }
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
