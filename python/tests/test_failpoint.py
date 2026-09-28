from __future__ import annotations

import io
import json
import unittest
from unittest.mock import patch

from perimeter import failpoint


class FailpointTests(unittest.TestCase):
    def test_ignored_without_test_profile(self) -> None:
        env = {"COURSE_TEST_PROFILE": "0", "COURSE_FAILPOINT": "after_outbox_claim"}
        with patch.dict("os.environ", env, clear=False):
            with patch("sys.stdout", new_callable=io.StringIO) as stdout:
                with patch("perimeter.failpoint.time.sleep", side_effect=AssertionError("must not hang")):
                    failpoint.hit("after_outbox_claim", "outbox-dispatcher")
        self.assertEqual(stdout.getvalue(), "")

    def test_ignored_when_name_differs(self) -> None:
        env = {"COURSE_TEST_PROFILE": "1", "COURSE_FAILPOINT": "after_provider_response"}
        with patch.dict("os.environ", env, clear=False):
            with patch("sys.stdout", new_callable=io.StringIO) as stdout:
                with patch("perimeter.failpoint.time.sleep", side_effect=AssertionError("must not hang")):
                    failpoint.hit("after_outbox_claim", "outbox-dispatcher")
        self.assertEqual(stdout.getvalue(), "")

    def test_writes_ack_then_stops_when_asked(self) -> None:
        env = {"COURSE_TEST_PROFILE": "1", "COURSE_FAILPOINT": "after_outbox_claim"}
        calls = {"n": 0}

        def stop() -> bool:
            calls["n"] += 1
            return calls["n"] > 1

        with patch.dict("os.environ", env, clear=False):
            with patch("sys.stdout", new_callable=io.StringIO) as stdout:
                with patch("perimeter.failpoint.time.sleep", return_value=None):
                    failpoint.hit("after_outbox_claim", "outbox-dispatcher", should_stop=stop)
        payload = json.loads(stdout.getvalue().strip())
        self.assertEqual(payload["event"], "failpoint.reached")
        self.assertEqual(payload["name"], "after_outbox_claim")
        self.assertEqual(payload["instanceId"], "outbox-dispatcher")


if __name__ == "__main__":
    unittest.main()