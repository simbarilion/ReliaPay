"""Однострочный JSON formatter для stdout периметра. Failpoint ack пишется через print и не проходит сюда."""

from __future__ import annotations

import json
import logging
import sys
from datetime import datetime, timezone
from typing import Any

_SAFE_KEYS = frozenset(
    {
        "correlationId",
        "requestId",
        "module",
        "action",
        "outcome",
        "status",
        "durationMs",
        "operationId",
        "processId",
        "jobId",
        "executionId",
        "attemptId",
        "leaseVersion",
        "externalRequestId",
        "outboxId",
        "errorCode",
        "service",
        "event",
        "instanceId",
    }
)

_FORBIDDEN_FRAGMENTS = (
    "jwt",
    "hmac",
    "password",
    "secret",
    "signature",
    "authorization",
    "payload",
    "receipt",
    "reason",
    "message",
    "body",
    "cookie",
)

_EXTRA_ATTR = "reliapay_fields"


class JsonFormatter(logging.Formatter):
    """Каждая запись — один JSON-объект без отступов."""

    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, Any] = {
            "timestamp": datetime.now(timezone.utc)
            .isoformat(timespec="milliseconds")
            .replace("+00:00", "Z"),
            "level": record.levelname.lower(),
            "message": record.getMessage(),
            "logger": record.name,
        }
        fields = getattr(record, _EXTRA_ATTR, None)
        if isinstance(fields, dict):
            for key, value in fields.items():
                if key in _SAFE_KEYS and value is not None and not is_forbidden_field(key):
                    payload[key] = value
        if record.exc_info and "errorCode" not in payload:
            payload["errorCode"] = "internal.error"
        return json.dumps(payload, ensure_ascii=True, separators=(",", ":"), default=str)


def is_forbidden_field(name: str) -> bool:
    lowered = name.lower()
    return any(fragment in lowered for fragment in _FORBIDDEN_FRAGMENTS)


def configure(service: str, *, level: int = logging.INFO) -> logging.Logger:
    """Настраивает root logging на JSON stdout один раз на процесс."""
    root_logger = logging.getLogger()
    if getattr(root_logger, "_reliapay_json", False):
        return logging.getLogger(service)

    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(JsonFormatter())
    root_logger.handlers.clear()
    root_logger.addHandler(handler)
    root_logger.setLevel(level)
    root_logger._reliapay_json = True  # type: ignore[attr-defined]
    logger = logging.getLogger(service)
    logger.setLevel(level)
    return logger


def safe_fields(**fields: Any) -> dict[str, Any]:
    """Оставляет только разрешённые ключи для extra."""
    return {
        key: value
        for key, value in fields.items()
        if key in _SAFE_KEYS and value is not None and not is_forbidden_field(key)
    }


def emit(logger: logging.Logger, level: int, message: str, **fields: Any) -> None:
    """Пишет JSON-строку с безопасными полями без коллизий с атрибутами LogRecord."""
    logger.log(level, message, extra={_EXTRA_ATTR: safe_fields(**fields)})


def info(logger: logging.Logger, message: str, **fields: Any) -> None:
    emit(logger, logging.INFO, message, **fields)
