from __future__ import annotations

import unittest
from unittest.mock import patch

from perimeter.config import AdapterConfig, ConfigError, DispatcherConfig, ReconcilerConfig


class ConfigTests(unittest.TestCase):
    def test_adapter_rejects_missing_secrets_and_urls(self) -> None:
        with patch.dict("os.environ", {}, clear=True):
            with self.assertRaises(ConfigError):
                AdapterConfig.from_env()

    def test_dispatcher_rejects_missing_provider_url(self) -> None:
        env = {
            "OUTBOX_OWNER": "outbox-dispatcher",
            "PGHOST": "postgres",
            "PGDATABASE": "course",
            "PGUSER": "outbox_dispatcher",
            "PGPASSWORD": "x",
        }
        with patch.dict("os.environ", env, clear=True):
            with self.assertRaises(ConfigError):
                DispatcherConfig.from_env()

    def test_reconciler_rejects_missing_database(self) -> None:
        with patch.dict("os.environ", {"COURSE_TEST_PROFILE": "1"}, clear=True):
            with self.assertRaises(ConfigError):
                ReconcilerConfig.from_env()

    def test_adapter_loads_raw_secret_without_trim(self) -> None:
        env = {
            "PROVIDER_CALLBACK_CAPABILITY": "cap",
            "PROVIDER_CALLBACK_TOKEN": " token ",
            "PROVIDER_HMAC_SECRET": " hmac ",
            "RECEIPT_API_URL": "http://gateway:8080/api/receipt/accept",
        }
        with patch.dict("os.environ", env, clear=True):
            cfg = AdapterConfig.from_env()
        self.assertEqual(cfg.hmac_secret, " hmac ")
        self.assertEqual(cfg.callback_token, " token ")


if __name__ == "__main__":
    unittest.main()
