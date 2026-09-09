from __future__ import annotations

import http.client
import socket
import urllib.parse


class TransportError(OSError):
    """Timeout или соединение с зависимостью не установлено."""


def post_bytes(
    url: str,
    body: bytes,
    headers: dict[str, str],
    timeout: float,
) -> tuple[int, bytes, str | None]:
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != "http" or not parsed.hostname:
        raise TransportError("unsupported url")
    port = parsed.port or 80
    path = parsed.path or "/"
    if parsed.query:
        path = path + "?" + parsed.query
    connection = http.client.HTTPConnection(parsed.hostname, port, timeout=timeout)
    try:
        connection.request("POST", path, body=body, headers=headers)
        response = connection.getresponse()
        payload = response.read()
        return response.status, payload, response.getheader("Content-Type")
    except (TimeoutError, socket.timeout, ConnectionError, http.client.HTTPException, OSError) as error:
        raise TransportError(str(error)) from error
    finally:
        connection.close()
