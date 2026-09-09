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
            listen_port=8080,
            api_timeout_sec=8.0,
        )


@dataclass(frozen=True)
class DispatcherConfig:
    owner: str
    provider_url: str
    poll_sec: float
    timeout_sec: float

    @staticmethod
    def from_env() -> DispatcherConfig:
        test = _test_profile()
        return DispatcherConfig(
            owner=_require("OUTBOX_OWNER"),
            provider_url=_require("PROVIDER_URL").rstrip("/"),
            poll_sec=0.2 if test else 1.0,
            timeout_sec=0.5 if test else 5.0,
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
        return ReconcilerConfig(
            poll_sec=0.2 if test else 1.0,
            batch_limit=32,
        )
