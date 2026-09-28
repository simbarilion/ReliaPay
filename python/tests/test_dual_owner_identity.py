from __future__ import annotations

import os
import unittest
from unittest.mock import patch

from perimeter.config import DispatcherConfig
from perimeter.provider_body import provider_payment_bytes


class DualOwnerAndIdentityTests(unittest.TestCase):
    def test_outbox_owner_is_read_from_env_per_instance(self) -> None:
        base = {
            "OUTBOX_OWNER": "outbox-dispatcher",
            "PROVIDER_URL": "http://provider-simulator:8081",
            "COURSE_TEST_PROFILE": "1",
        }
        with patch.dict(os.environ, base, clear=False):
            settings_a = DispatcherConfig.from_env()
        with patch.dict(
            os.environ,
            {**base, "OUTBOX_OWNER": "outbox-dispatcher-b"},
            clear=False,
        ):
            settings_b = DispatcherConfig.from_env()
        self.assertEqual(settings_a.owner, "outbox-dispatcher")
        self.assertEqual(settings_b.owner, "outbox-dispatcher-b")
        self.assertNotEqual(settings_a.owner, settings_b.owner)

    def test_external_request_body_stable_across_owners(self) -> None:
        body = provider_payment_bytes("same-execution-id", "10.00", "RUB")
        self.assertEqual(
            body,
            b'{"operationId":"same-execution-id","amount":"10.00","currency":"RUB"}',
        )


if __name__ == "__main__":
    unittest.main()
