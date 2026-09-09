from __future__ import annotations

import unittest

from perimeter.legacy import LegacyCallbackError, parse_legacy_callback


class LegacyCallbackTests(unittest.TestCase):
    def test_accepts_required_fields_only(self) -> None:
        body = (
            b'{"providerPaymentId":"p-1","operationId":"e-1",'
            b'"result":"COMPLETED","message":"ok","occurredAt":"2026-09-04T12:00:00.123Z"}'
        )
        parsed = parse_legacy_callback(body)
        self.assertEqual(parsed["providerPaymentId"], "p-1")
        self.assertEqual(parsed["occurredAt"], "2026-09-04T12:00:00.123Z")

    def test_rejects_unknown_field(self) -> None:
        body = (
            b'{"providerPaymentId":"p-1","operationId":"e-1","result":"COMPLETED",'
            b'"message":"ok","occurredAt":"2026-09-04T12:00:00Z","extra":1}'
        )
        with self.assertRaises(LegacyCallbackError):
            parse_legacy_callback(body)

    def test_rejects_crlf_in_transport_strings(self) -> None:
        body = (
            b'{"providerPaymentId":"p-1\\n","operationId":"e-1","result":"COMPLETED",'
            b'"message":"ok","occurredAt":"2026-09-04T12:00:00Z"}'
        )
        with self.assertRaises(LegacyCallbackError):
            parse_legacy_callback(body)

    def test_rejects_occurred_at_without_z(self) -> None:
        body = (
            b'{"providerPaymentId":"p-1","operationId":"e-1","result":"COMPLETED",'
            b'"message":"ok","occurredAt":"2026-09-04T12:00:00+00:00"}'
        )
        with self.assertRaises(LegacyCallbackError):
            parse_legacy_callback(body)

    def test_rejects_invalid_utf8(self) -> None:
        with self.assertRaises(LegacyCallbackError):
            parse_legacy_callback(b'{"providerPaymentId":"\xff"}')


if __name__ == "__main__":
    unittest.main()
