"""Служебные HTTP-эндпоинты live/ready/metrics для perimeter-процессов."""

from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Callable
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

ReadyCheck = Callable[[], bool]
OPENMETRICS_TYPE = "application/openmetrics-text; version=1.0.0; charset=utf-8"
_EMPTY_METRICS = b"# EOF\n"


def probe_http_live(url: str, timeout_sec: float = 2.0) -> bool:
    """True, если GET url вернул HTTP 200 (для ready adapter → gateway)."""
    try:
        req = Request(url, method="GET")
        with urlopen(req, timeout=timeout_sec) as response:  # noqa: S310
            return int(response.status) == 200
    except Exception:
        return False


def gateway_live_url_from_receipt_api(receipt_api_url: str) -> str:
    parts = urlsplit(receipt_api_url)
    return f"{parts.scheme}://{parts.netloc}/health/live"


def start_obs_server(
    *,
    host: str,
    port: int,
    ready_check: ReadyCheck,
    metrics_body: Callable[[], bytes] | None = None,
) -> ThreadingHTTPServer:
    """Поднимает ThreadingHTTPServer с /health/live|ready и /metrics в фоне."""

    metrics_fn = metrics_body or (lambda: _EMPTY_METRICS)

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self) -> None:  # noqa: N802
            path = urlsplit(self.path).path
            if path == "/health/live":
                self._json(200, {"status": "live"})
                return
            if path == "/health/ready":
                try:
                    ok = bool(ready_check())
                except Exception:
                    ok = False
                if ok:
                    self._json(200, {"status": "ready"})
                else:
                    self._json(
                        503,
                        {"status": "not_ready", "code": "dependency.unavailable"},
                    )
                return
            if path == "/metrics":
                body = metrics_fn()
                self.send_response(200)
                self.send_header("Content-Type", OPENMETRICS_TYPE)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def _json(self, status: int, payload: dict) -> None:
            raw = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)

        def log_message(self, format: str, *args: object) -> None:
            del format, args

    server = ThreadingHTTPServer((host, port), Handler)
    thread = threading.Thread(target=server.serve_forever, name=f"obs-{port}", daemon=True)
    thread.start()
    return server


def pg_ready(connection) -> bool:
    """Проверка доступности PostgreSQL через уже открытое соединение."""
    try:
        with connection.cursor() as cursor:
            cursor.execute("SELECT 1")
            cursor.fetchone()
        return True
    except Exception:
        return False
