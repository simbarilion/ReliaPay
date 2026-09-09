from __future__ import annotations

import json
import re
from typing import Any

_OCCURRED_AT = re.compile(
    r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?Z$"
)
_REQUIRED = ("providerPaymentId", "operationId", "result", "message", "occurredAt")
_MAX_ID = 128
_MAX_MESSAGE = 500
_MAX_OCCURRED = 64


class LegacyCallbackError(ValueError):
    """Legacy provider callback не проходит schema."""


def parse_legacy_callback(body: bytes) -> dict[str, str]:
    try:
        text = body.decode("utf-8")
    except UnicodeDecodeError as error:
        raise LegacyCallbackError("invalid utf-8") from error
    try:
        payload: Any = json.loads(text)
    except json.JSONDecodeError as error:
        raise LegacyCallbackError("invalid json") from error
    if not isinstance(payload, dict) or any(key not in _REQUIRED for key in payload):
        raise LegacyCallbackError("unexpected fields")
    if any(key not in payload for key in _REQUIRED):
        raise LegacyCallbackError("missing fields")
    if payload["result"] not in {"COMPLETED", "REJECTED"}:
        raise LegacyCallbackError("invalid result")
    parsed: dict[str, str] = {}
    for key in _REQUIRED:
        value = payload[key]
        if not isinstance(value, str):
            raise LegacyCallbackError(f"{key} must be a string")
        if "\r" in value or "\n" in value:
            raise LegacyCallbackError(f"{key} contains crlf")
        parsed[key] = value
    for key in ("providerPaymentId", "operationId"):
        if not 1 <= len(parsed[key]) <= _MAX_ID:
            raise LegacyCallbackError(f"{key} length")
    if len(parsed["message"]) > _MAX_MESSAGE:
        raise LegacyCallbackError("message length")
    if len(parsed["occurredAt"]) > _MAX_OCCURRED or _OCCURRED_AT.fullmatch(parsed["occurredAt"]) is None:
        raise LegacyCallbackError("occurredAt")
    return parsed
