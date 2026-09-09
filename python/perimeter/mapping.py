from __future__ import annotations

import json
from typing import Mapping


def map_legacy_to_receipt(legacy: Mapping[str, str]) -> dict[str, object]:
    provider_id = legacy["providerPaymentId"]
    return {
        "externalRequestId": legacy["operationId"],
        "messageId": provider_id,
        "occurredAt": legacy["occurredAt"],
        "outcome": legacy["result"],
        "providerPaymentId": provider_id,
        "version": 1,
    }


def compact_receipt_bytes(receipt: Mapping[str, object]) -> bytes:
    return json.dumps(
        dict(receipt),
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")
