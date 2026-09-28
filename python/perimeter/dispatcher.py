from __future__ import annotations

import signal
import time
from dataclasses import dataclass
from uuid import UUID

import psycopg

from perimeter.classify import AttemptKind, classify_provider_attempt
from perimeter.config import ConfigError, DispatcherConfig
from perimeter.httpclient import TransportError, post_bytes
from perimeter.provider_body import provider_payment_bytes

_STOP = False


@dataclass(frozen=True)
class Claim:
    outbox_id: UUID
    lease_version: int
    external_request_id: str
    correlation_id: UUID
    amount: str
    currency: str


def _handle_stop(signum: int, _frame: object) -> None:
    del signum
    global _STOP
    _STOP = True


def run() -> None:
    try:
        cfg = DispatcherConfig.from_env()
    except ConfigError as error:
        raise SystemExit(str(error)) from error
    signal.signal(signal.SIGTERM, _handle_stop)
    signal.signal(signal.SIGINT, _handle_stop)
    with psycopg.connect(autocommit=True) as connection:
        _apply_outbox_settings(connection, cfg)
        while not _STOP:
            claims = _claim(connection, cfg.owner)
            if not claims:
                time.sleep(cfg.poll_sec)
                continue
            for claim in claims:
                if _STOP:
                    return
                _deliver(connection, cfg, claim)


def _apply_outbox_settings(connection: psycopg.Connection, cfg: DispatcherConfig) -> None:
    """Прокидывает COURSE_OUTBOX_* в session GUC для claim/fail (сигнатуры SQL фиксированы)."""
    with connection.cursor() as cursor:
        cursor.execute(
            "SELECT set_config('app.outbox_lease_ms', %s, false),"
            " set_config('app.outbox_max_attempts', %s, false),"
            " set_config('app.outbox_backoff_base_ms', %s, false),"
            " set_config('app.outbox_backoff_max_ms', %s, false),"
            " set_config('app.outbox_jitter_max_ms', %s, false)",
            (
                str(cfg.lease_ms),
                str(cfg.max_attempts),
                str(cfg.backoff_base_ms),
                str(cfg.backoff_max_ms),
                str(cfg.jitter_max_ms),
            ),
        )


def _claim(connection: psycopg.Connection, owner: str) -> list[Claim]:
    with connection.cursor() as cursor:
        cursor.execute(
            "SELECT outbox_id, lease_version, external_request_id, "
            "correlation_id, amount, currency "
            "FROM delivery.claim_outbox(%s, %s)",
            (owner, 1),
        )
        rows = cursor.fetchall()
    claims: list[Claim] = []
    for row in rows:
        claims.append(
            Claim(
                outbox_id=row[0],
                lease_version=int(row[1]),
                external_request_id=str(row[2]),
                correlation_id=row[3],
                amount=str(row[4]),
                currency=str(row[5]),
            )
        )
    return claims


def _deliver(connection: psycopg.Connection, cfg: DispatcherConfig, claim: Claim) -> None:
    body = provider_payment_bytes(claim.external_request_id, claim.amount, claim.currency)
    headers = {
        "Content-Type": "application/json",
        "Idempotency-Key": claim.external_request_id,
        "X-Correlation-ID": str(claim.correlation_id),
    }
    url = cfg.provider_url + "/payments"
    try:
        status, payload, _media = post_bytes(url, body, headers, cfg.timeout_sec)
        result = classify_provider_attempt(status, payload)
    except (TimeoutError, TransportError, OSError):
        result = classify_provider_attempt(transport_error=True)
    if result.kind == AttemptKind.SUCCEED:
        _succeed(connection, cfg.owner, claim, result.provider_payment_id or "")
        return
    _fail(connection, cfg.owner, claim, result.error_code or "transport.error.retryable")


def _succeed(
    connection: psycopg.Connection,
    owner: str,
    claim: Claim,
    provider_payment_id: str,
) -> None:
    with connection.cursor() as cursor:
        cursor.execute(
            "SELECT delivery.succeed_outbox(%s, %s, %s, %s)",
            (claim.outbox_id, owner, claim.lease_version, provider_payment_id),
        )


def _fail(
    connection: psycopg.Connection,
    owner: str,
    claim: Claim,
    error_code: str,
) -> None:
    with connection.cursor() as cursor:
        cursor.execute(
            "SELECT delivery.fail_outbox(%s, %s, %s, %s)",
            (claim.outbox_id, owner, claim.lease_version, error_code),
        )