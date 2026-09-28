from __future__ import annotations

import json
import logging
import unittest
from io import StringIO

from perimeter import jsonlog
from perimeter.jsonlog import JsonFormatter, configure, is_forbidden_field


class JsonLogTests(unittest.TestCase):
    def test_formatter_emits_one_json_object(self) -> None:
        stream = StringIO()
        handler = logging.StreamHandler(stream)
        handler.setFormatter(JsonFormatter())
        logger = logging.getLogger("test.jsonlog.formatter")
        logger.handlers.clear()
        logger.addHandler(handler)
        logger.setLevel(logging.INFO)
        logger.propagate = False
        jsonlog.info(
            logger,
            "outbox delivery completed",
            correlationId="aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            externalRequestId="ext-1",
            leaseVersion=3,
            status="ok",
            durationMs=12.5,
            module="payment",
            action="prepare",
        )
        line = stream.getvalue().strip()
        payload = json.loads(line)
        self.assertEqual(payload["message"], "outbox delivery completed")
        self.assertEqual(payload["correlationId"], "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        self.assertEqual(payload["externalRequestId"], "ext-1")
        self.assertEqual(payload["module"], "payment")
        self.assertEqual(payload["action"], "prepare")
        self.assertNotEqual(payload.get("name"), logger.name)
        self.assertNotIn("name", payload)
        self.assertNotIn("Authorization", line)
        self.assertNotIn("payload", payload)

    def test_forbidden_field_names(self) -> None:
        self.assertTrue(is_forbidden_field("reason"))
        self.assertTrue(is_forbidden_field("callback.message"))
        self.assertTrue(is_forbidden_field("hmacSecret"))
        self.assertFalse(is_forbidden_field("correlationId"))

    def test_configure_idempotent(self) -> None:
        first = configure("unit-service")
        second = configure("unit-service")
        self.assertEqual(first.name, second.name)


if __name__ == "__main__":
    unittest.main()
