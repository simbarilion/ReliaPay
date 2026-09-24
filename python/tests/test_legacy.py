from __future__ import annotations

import json
import unittest

from perimeter.legacy import LegacyCallbackError, parse_legacy_callback


def _body(**overrides: object) -> bytes:
    payload = {
        "providerPaymentId": "p-1",
        "operationId": "e-1",
        "result": "COMPLETED",
        "message": "ok",
        "occurredAt": "2026-09-04T12:00:00.123Z",
    }
    payload.update(overrides)
    return json.dumps(payload, separators=(",", ":")).encode("utf-8")


class LegacyCallbackTests(unittest.TestCase):
    def test_accepts_required_fields_only(self) -> None:
        parsed = parse_legacy_callback(_body())
        self.assertEqual(parsed["providerPaymentId"], "p-1")
        self.assertEqual(parsed["occurredAt"], "2026-09-04T12:00:00.123Z")

    def test_rejects_unknown_field(self) -> None:
        raw = (
            b'{"providerPaymentId":"p-1","operationId":"e-1","result":"COMPLETED",'
            b'"message":"ok","occurredAt":"2026-09-04T12:00:00Z","extra":1}'
        )
        with self.assertRaises(LegacyCallbackError):
            parse_legacy_callback(raw)

    def test_rejects_crlf_in_transport_strings(self) -> None:
        with self.assertRaises(LegacyCallbackError):
            parse_legacy_callback(_body(providerPaymentId="p-1\n"))

    def test_rejects_occurred_at_without_z(self) -> None:
        with self.assertRaises(LegacyCallbackError):
            parse_legacy_callback(_body(occurredAt="2026-09-04T12:00:00+00:00"))

    def test_rejects_invalid_utf8(self) -> None:
        with self.assertRaises(LegacyCallbackError):
            parse_legacy_callback(b'{"providerPaymentId":"\xff"}')

    def test_rejects_non_string_result_before_membership(self) -> None:
        for bad in (None, 1, True, False, {"x": 1}, ["COMPLETED"]):
            with self.subTest(bad=bad):
                with self.assertRaises(LegacyCallbackError):
                    parse_legacy_callback(_body(result=bad))

    def test_rejects_non_string_required_fields(self) -> None:
        for key in ("providerPaymentId", "operationId", "message", "occurredAt"):
            with self.subTest(key=key):
                with self.assertRaises(LegacyCallbackError):
                    parse_legacy_callback(_body(**{key: 12}))

    def test_rejects_impossible_calendar_dates(self) -> None:
        for occurred in (
            "2026-02-30T12:00:00Z",
            "2026-13-01T12:00:00Z",
            "2026-04-31T12:00:00Z",
            "2026-00-10T12:00:00Z",
        ):
            with self.subTest(occurred=occurred):
                with self.assertRaises(LegacyCallbackError):
                    parse_legacy_callback(_body(occurredAt=occurred))

    def test_accepts_leap_day_and_whole_seconds(self) -> None:
        parsed = parse_legacy_callback(_body(occurredAt="2024-02-29T00:00:00Z"))
        self.assertEqual(parsed["occurredAt"], "2024-02-29T00:00:00Z")


if __name__ == "__main__":
    unittest.main()
