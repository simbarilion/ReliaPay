from __future__ import annotations

import unittest

from perimeter.provider_body import provider_payment_bytes


class StableBodyTests(unittest.TestCase):
    def test_retries_reuse_exact_key_and_body_bytes(self) -> None:
        first = provider_payment_bytes("ext-1", "1000.00", "RUB")
        second = provider_payment_bytes("ext-1", "1000.00", "RUB")
        self.assertEqual(first, second)
        self.assertEqual(
            first,
            b'{"operationId":"ext-1","amount":"1000.00","currency":"RUB"}',
        )


if __name__ == "__main__":
    unittest.main()
