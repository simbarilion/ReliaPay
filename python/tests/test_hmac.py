from __future__ import annotations

import hashlib
import hmac
import unittest

from perimeter.crypto import sign_body_v1


class HmacTests(unittest.TestCase):
    def test_signs_exact_bytes_with_raw_utf8_secret(self) -> None:
        secret = " leading-space-and-not-base64 "
        body = b'{"messageId":"p-1","version":1}'
        header = sign_body_v1(secret, body)
        expected = hmac.new(secret.encode("utf-8"), body, hashlib.sha256).hexdigest()
        self.assertEqual(header, "v1=" + expected)
        self.assertEqual(header, header.lower())

        trimmed = hmac.new(b"leading-space-and-not-base64", body, hashlib.sha256).hexdigest()
        self.assertNotEqual(header, "v1=" + trimmed)


if __name__ == "__main__":
    unittest.main()
