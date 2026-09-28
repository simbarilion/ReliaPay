from __future__ import annotations

import os
from dataclasses import dataclass


class ConfigError(ValueError):
    """Обязательная переменная окружения отсутствует."""


def _require(name: str) -> str:
    value = os.environ.get(name)
    if value is None or value == "":
        raise ConfigError(f"missing {name}")
    return value


def _test_profile() -> bool:
    return os.environ.get("COURSE_TEST_PROFILE") == "1"


def _env_ms(name: str, default_ms: float) -> float:
    raw = os.environ.get(name)
    if raw is None or raw == "":
        return default_ms / 1000.0
    try:
        return max(0.001, float(raw) / 1000.0)
    except ValueError as error:
        raise ConfigError(f"invalid {name}") from error


def _env_int(name: str, default: int, *, minimum: int = 0, maximum: int = 1_000_000) -> int:
    raw = os.environ.get(name)
    if raw is None or raw == "":
        return default
    try:
        value = int(raw)
    except ValueError as error:
        raise ConfigError(f"invalid {name}") from error
    return max(minimum, min(maximum, value))


@dataclass(frozen=True)
class AdapterConfig:
    capability: str
    callback_token: str
    hmac_secret: str
    receipt_api_url: str
    listen_host: str
    listen_port: int
    api_timeout_sec: float

    @staticmethod
    def from_env() -> AdapterConfig:
        return AdapterConfig(
            capability=_require("PROVIDER_CALLBACK_CAPABILITY"),
            callback_token=_require("PROVIDER_CALLBACK_TOKEN"),
            hmac_secret=_require("PROVIDER_HMAC_SECRET"),
            receipt_api_url=_require("RECEIPT_API_URL"),
            listen_host="0.0.0.0",
            listen_port=8082,
            api_timeout_sec=8.0,
        )


@dataclass(frozen=True)
class DispatcherConfig:
    owner: str
    provider_url: str
    poll_sec: float
    timeout_sec: float
    max_attempts: int
    backoff_base_ms: int
    backoff_max_ms: int
    jitter_max_ms: int
    lease_ms: int

    @staticmethod
    def from_env() -> DispatcherConfig:
        test = _test_profile()
        default_poll_ms = 100.0 if test else 1000.0
        default_timeout_ms = 500.0 if test else 5000.0
        default_max_attempts = 4 if test else 8
        default_base_ms = 200 if test else 1000
        default_backoff_max_ms = 800 if test else 30000
        default_jitter_ms = 100 if test else 500
        default_lease_ms = 2000 if test else 30000
        return DispatcherConfig(
            owner=_require("OUTBOX_OWNER"),
            provider_url=_require("PROVIDER_URL").rstrip("/"),
            poll_sec=_env_ms("COURSE_OUTBOX_POLL_MS", default_poll_ms),
            timeout_sec=_env_ms("COURSE_PROVIDER_TIMEOUT_MS", default_timeout_ms),
            max_attempts=_env_int(
                "COURSE_OUTBOX_MAX_ATTEMPTS", default_max_attempts, minimum=1, maximum=32
            ),
            backoff_base_ms=_env_int(
                "COURSE_OUTBOX_BACKOFF_BASE_MS", default_base_ms, minimum=1, maximum=60000
            ),
            backoff_max_ms=_env_int(
                "COURSE_OUTBOX_BACKOFF_MAX_MS", default_backoff_max_ms, minimum=1, maximum=300000
            ),
            jitter_max_ms=_env_int(
                "COURSE_OUTBOX_JITTER_MAX_MS", default_jitter_ms, minimum=0, maximum=60000
            ),
            lease_ms=_env_int(
                "COURSE_OUTBOX_LEASE_MS", default_lease_ms, minimum=1, maximum=30000
            ),
        )


@dataclass(frozen=True)
class ReconcilerConfig:
    poll_sec: float
    batch_limit: int

    @staticmethod
    def from_env() -> ReconcilerConfig:
        _require("PGHOST")
        _require("PGDATABASE")
        _require("PGUSER")
        test = _test_profile()
        default_poll_ms = 500.0 if test else 1000.0
        return ReconcilerConfig(
            poll_sec=_env_ms("COURSE_INBOX_POLL_MS", default_poll_ms),
            batch_limit=32,
        )