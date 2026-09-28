from __future__ import annotations

import json
import os
import time
from collections.abc import Callable


AFTER_OUTBOX_CLAIM = "after_outbox_claim"
AFTER_PROVIDER_RESPONSE = "after_provider_response"


def hit(
    name: str,
    instance_id: str,
    *,
    should_stop: Callable[[], bool] | None = None,
) -> None:
    """Пишет JSON-ack и зависает только при COURSE_TEST_PROFILE=1 и совпавшем COURSE_FAILPOINT."""
    if os.environ.get("COURSE_TEST_PROFILE") != "1":
        return
    configured = (os.environ.get("COURSE_FAILPOINT") or "").strip()
    if not configured or configured != name:
        return
    if not instance_id:
        instance_id = "unknown"
    line = json.dumps(
        {"event": "failpoint.reached", "name": name, "instanceId": instance_id},
        ensure_ascii=True,
        separators=(",", ":"),
    )
    print(line, flush=True)
    while True:
        if should_stop is not None and should_stop():
            return
        time.sleep(1.0)