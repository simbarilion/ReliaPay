from __future__ import annotations

import unittest

from perimeter.adapter import AdapterResponse, handle_callback


class AdapterHttpTests(unittest.TestCase):
    def test_wrong_capability_is_empty_404(self) -> None:
        result = handle_callback(
            path="/callbacks/provider-v02/other",
            capability="cap",
            content_length="2",
            body=b"{}",
            hmac_secret="secret",
            callback_token="tok",
            receipt_api_url="http://gateway:8080/api/receipt/accept",
            post_receipt=_fail_post,
        )
        self.assertEqual(result, AdapterResponse(404, b"", None))

    def test_invalid_json_is_empty_400(self) -> None:
        result = handle_callback(
            path="/callbacks/provider-v02/cap",
            capability="cap",
            content_length="3",
            body=b"{x}",
            hmac_secret="secret",
            callback_token="tok",
            receipt_api_url="http://gateway:8080/api/receipt/accept",
            post_receipt=_fail_post,
        )
        self.assertEqual(result, AdapterResponse(400, b"", None))

    def test_maps_api_success_and_client_error(self) -> None:
        body = (
            b'{"providerPaymentId":"p-1","operationId":"e-1","result":"COMPLETED",'
            b'"message":"ok","occurredAt":"2026-09-04T12:00:00Z"}'
        )
        captured: dict[str, object] = {}

        def post_ok(url: str, payload: bytes, headers: dict[str, str], timeout: float):
            del timeout
            captured["url"] = url
            captured["payload"] = payload
            captured["headers"] = headers
            return 200, b'{"status":"ok"}', "application/json"

        ok = handle_callback(
            path="/callbacks/provider-v02/cap",
            capability="cap",
            content_length=str(len(body)),
            body=body,
            hmac_secret="secret",
            callback_token="tok",
            receipt_api_url="http://gateway:8080/api/receipt/accept",
            post_receipt=post_ok,
        )
        self.assertEqual(ok.status, 200)
        self.assertEqual(ok.body, b'{"status":"ok"}')
        self.assertEqual(ok.content_type, "application/json")
        headers = captured["headers"]
        assert isinstance(headers, dict)
        self.assertEqual(headers["Authorization"], "Bearer tok")
        self.assertEqual(headers["Idempotency-Key"], "p-1")
        self.assertEqual(headers["X-Action-Version"], "1")
        self.assertTrue(headers["X-Provider-Signature"].startswith("v1="))
        self.assertEqual(captured["url"], "http://gateway:8080/api/receipt/accept")

        def post_409(url: str, payload: bytes, headers: dict[str, str], timeout: float):
            del url, payload, headers, timeout
            return 409, b'{"status":"error","code":"idempotency.conflict"}', "application/json"

        conflict = handle_callback(
            path="/callbacks/provider-v02/cap",
            capability="cap",
            content_length=str(len(body)),
            body=body,
            hmac_secret="secret",
            callback_token="tok",
            receipt_api_url="http://gateway:8080/api/receipt/accept",
            post_receipt=post_409,
        )
        self.assertEqual(conflict.status, 409)

    def test_api_timeout_is_503_dependency(self) -> None:
        body = (
            b'{"providerPaymentId":"p-1","operationId":"e-1","result":"COMPLETED",'
            b'"message":"ok","occurredAt":"2026-09-04T12:00:00Z"}'
        )

        def post_timeout(url: str, payload: bytes, headers: dict[str, str], timeout: float):
            del url, payload, headers, timeout
            raise TimeoutError("api timeout")

        result = handle_callback(
            path="/callbacks/provider-v02/cap",
            capability="cap",
            content_length=str(len(body)),
            body=body,
            hmac_secret="secret",
            callback_token="tok",
            receipt_api_url="http://gateway:8080/api/receipt/accept",
            post_receipt=post_timeout,
        )
        self.assertEqual(result.status, 503)
        self.assertEqual(result.body, b'{"status":"error","code":"dependency.unavailable"}')
        self.assertEqual(result.content_type, "application/json")


def _fail_post(url: str, payload: bytes, headers: dict[str, str], timeout: float):
    del url, payload, headers, timeout
    raise AssertionError("API must not be called")


if __name__ == "__main__":
    unittest.main()
