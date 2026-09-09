from __future__ import annotations

import signal
import threading
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Callable
from urllib.parse import urlsplit

from perimeter.config import AdapterConfig, ConfigError
from perimeter.crypto import sign_body_v1
from perimeter.httpclient import TransportError, post_bytes
from perimeter.legacy import LegacyCallbackError, parse_legacy_callback
from perimeter.mapping import compact_receipt_bytes, map_legacy_to_receipt

PostReceipt = Callable[[str, bytes, dict[str, str], float], tuple[int, bytes, str | None]]
_MAX_BODY = 64 * 1024
_DEPENDENCY = b'{"status":"error","code":"dependency.unavailable"}'


@dataclass(frozen=True)
class AdapterResponse:
    status: int
    body: bytes
    content_type: str | None


def handle_callback(
    *,
    path: str,
    capability: str,
    content_length: str | None,
    body: bytes,
    hmac_secret: str,
    callback_token: str,
    receipt_api_url: str,
    post_receipt: PostReceipt,
    api_timeout_sec: float = 8.0,
) -> AdapterResponse:
    expected = "/callbacks/provider-v02/" + capability
    if urlsplit(path).path != expected:
        return AdapterResponse(404, b"", None)
    if not _content_length_ok(content_length, body):
        return AdapterResponse(400, b"", None)
    try:
        legacy = parse_legacy_callback(body)
    except LegacyCallbackError:
        return AdapterResponse(400, b"", None)
    receipt = map_legacy_to_receipt(legacy)
    payload = compact_receipt_bytes(receipt)
    headers = {
        "Content-Type": "application/json",
        "Authorization": "Bearer " + callback_token,
        "Idempotency-Key": str(receipt["messageId"]),
        "X-Action-Version": "1",
        "X-Provider-Signature": sign_body_v1(hmac_secret, payload),
    }
    try:
        status, response_body, media = post_receipt(
            receipt_api_url,
            payload,
            headers,
            api_timeout_sec,
        )
    except (TimeoutError, TransportError, OSError, ConnectionError):
        return AdapterResponse(503, _DEPENDENCY, "application/json")
    return AdapterResponse(status, response_body, media)


def _content_length_ok(content_length: str | None, body: bytes) -> bool:
    if content_length is None:
        return False
    try:
        declared = int(content_length)
    except ValueError:
        return False
    return 0 <= declared <= _MAX_BODY and declared == len(body)


def run() -> None:
    try:
        cfg = AdapterConfig.from_env()
    except ConfigError as error:
        raise SystemExit(str(error)) from error

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self) -> None:  # noqa: N802
            length_header = self.headers.get("Content-Length")
            try:
                declared = int(length_header) if length_header is not None else -1
            except ValueError:
                declared = -1
            if declared < 0 or declared > _MAX_BODY:
                raw = b""
                result = AdapterResponse(400, b"", None)
            else:
                raw = self.rfile.read(declared)
                result = handle_callback(
                    path=self.path,
                    capability=cfg.capability,
                    content_length=length_header,
                    body=raw,
                    hmac_secret=cfg.hmac_secret,
                    callback_token=cfg.callback_token,
                    receipt_api_url=cfg.receipt_api_url,
                    post_receipt=post_bytes,
                    api_timeout_sec=cfg.api_timeout_sec,
                )
            self.send_response(result.status)
            if result.content_type:
                self.send_header("Content-Type", result.content_type)
            self.send_header("Content-Length", str(len(result.body)))
            self.end_headers()
            if result.body:
                self.wfile.write(result.body)

        def do_GET(self) -> None:  # noqa: N802
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def send_error(self, code: int, message: str | None = None, explain: str | None = None) -> None:
            del message, explain
            self.send_response(code)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def log_message(self, format: str, *args: object) -> None:
            del format, args

    server = ThreadingHTTPServer((cfg.listen_host, cfg.listen_port), Handler)

    def _stop(signum: int, _frame: object) -> None:
        del signum
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, _stop)
    signal.signal(signal.SIGINT, _stop)
    server.serve_forever()
