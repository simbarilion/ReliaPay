from __future__ import annotations

import unittest

from perimeter.mapping import compact_receipt_bytes, map_legacy_to_receipt


class MappingTests(unittest.TestCase):
    def test_maps_fields_and_keeps_timestamp_string(self) -> None:
        legacy = {
            "providerPaymentId": "provider-123",
            "operationId": "external-123",
            "result": "REJECTED",
            "message": "Payment completed",
            "occurredAt": "2026-09-04T12:00:00.123Z",
        }
        receipt = map_legacy_to_receipt(legacy)
        self.assertEqual(receipt["messageId"], "provider-123")
        self.assertEqual(receipt["providerPaymentId"], "provider-123")
        self.assertEqual(receipt["externalRequestId"], "external-123")
        self.assertEqual(receipt["outcome"], "REJECTED")
        self.assertEqual(receipt["occurredAt"], "2026-09-04T12:00:00.123Z")
        self.assertEqual(receipt["version"], 1)
        self.assertNotIn("message", receipt)

    def test_compact_sorted_json_has_no_bom_or_lf(self) -> None:
        receipt = {
            "outcome": "COMPLETED",
            "externalRequestId": "e-1",
            "messageId": "p-1",
            "occurredAt": "2026-09-04T12:00:00.123Z",
            "providerPaymentId": "p-1",
            "version": 1,
        }
        raw = compact_receipt_bytes(receipt)
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
        self.assertFalse(raw.endswith(b"\n"))
        self.assertEqual(
            raw,
            b'{"externalRequestId":"e-1","messageId":"p-1",'
            b'"occurredAt":"2026-09-04T12:00:00.123Z","outcome":"COMPLETED",'
            b'"providerPaymentId":"p-1","version":1}',
        )


if __name__ == "__main__":
    unittest.main()
