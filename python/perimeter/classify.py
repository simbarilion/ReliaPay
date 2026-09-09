from __future__ import annotations

import json
from dataclasses import dataclass
from enum import Enum


class AttemptKind(Enum):
    SUCCEED = "succeed"
    FAIL = "fail"


@dataclass(frozen=True)
class AttemptResult:
    kind: AttemptKind
    provider_payment_id: str | None = None
    error_code: str | None = None


def classify_provider_attempt(
    status: int | None = None,
    body: bytes | None = None,
    *,
    transport_error: bool = False,
) -> AttemptResult:
    if transport_error:
        return AttemptResult(AttemptKind.FAIL, error_code="transport.error.retryable")
    if status is None:
        return AttemptResult(AttemptKind.FAIL, error_code="transport.error.retryable")
    if status in {408, 429} or 500 <= status <= 599:
        return AttemptResult(AttemptKind.FAIL, error_code=f"http.{status}.retryable")
    if status != 202:
        return AttemptResult(AttemptKind.FAIL, error_code=f"http.{status}.terminal")
    return _parse_accepted(body or b"")


def _parse_accepted(body: bytes) -> AttemptResult:
    invalid = AttemptResult(AttemptKind.FAIL, error_code="response.invalid.terminal")
    try:
        payload = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return invalid
    if not isinstance(payload, dict) or set(payload.keys()) != {"providerPaymentId", "status"}:
        return invalid
    payment_id = payload.get("providerPaymentId")
    if payload.get("status") != "ACCEPTED" or not isinstance(payment_id, str) or payment_id == "":
        return invalid
    if "\r" in payment_id or "\n" in payment_id:
        return invalid
    return AttemptResult(AttemptKind.SUCCEED, provider_payment_id=payment_id)
