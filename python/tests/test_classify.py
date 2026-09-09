from __future__ import annotations

import unittest

from perimeter.classify import AttemptKind, classify_provider_attempt


class ClassifyTests(unittest.TestCase):
    def test_succeeds_on_202_accepted_only(self) -> None:
        result = classify_provider_attempt(
            202,
            b'{"providerPaymentId":"pay-1","status":"ACCEPTED"}',
        )
        self.assertEqual(result.kind, AttemptKind.SUCCEED)
        self.assertEqual(result.provider_payment_id, "pay-1")
        self.assertIsNone(result.error_code)

    def test_transport_timeout_is_retryable(self) -> None:
        result = classify_provider_attempt(transport_error=True)
        self.assertEqual(result.kind, AttemptKind.FAIL)
        self.assertEqual(result.error_code, "transport.error.retryable")

    def test_retryable_and_terminal_http(self) -> None:
        self.assertEqual(
            classify_provider_attempt(429, b"{}").error_code,
            "http.429.retryable",
        )
        self.assertEqual(
            classify_provider_attempt(503, b"{}").error_code,
            "http.503.retryable",
        )
        self.assertEqual(
            classify_provider_attempt(408, b"{}").error_code,
            "http.408.retryable",
        )
        self.assertEqual(
            classify_provider_attempt(400, b"{}").error_code,
            "http.400.terminal",
        )
        self.assertEqual(
            classify_provider_attempt(302, b"{}").error_code,
            "http.302.terminal",
        )

    def test_invalid_202_body_is_terminal(self) -> None:
        self.assertEqual(
            classify_provider_attempt(202, b'{"status":"ACCEPTED"}').error_code,
            "response.invalid.terminal",
        )
        self.assertEqual(
            classify_provider_attempt(202, b"\xff\xfe").error_code,
            "response.invalid.terminal",
        )
        extra = b'{"providerPaymentId":"pay-1","status":"ACCEPTED","extra":true}'
        self.assertEqual(
            classify_provider_attempt(202, extra).error_code,
            "response.invalid.terminal",
        )


if __name__ == "__main__":
    unittest.main()
