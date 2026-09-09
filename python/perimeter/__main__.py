"""Stage-0 idle entrypoint. Later stages replace the sleep loop with real workers."""

from __future__ import annotations

import os
import signal
import time

_STOP = False


def _handle_stop(signum: int, _frame: object) -> None:
    del signum
    global _STOP
    _STOP = True


def main() -> None:
    role = os.environ.get("PERIMETER_ROLE", "unknown")
    if role not in {"outbox-dispatcher", "receipt-adapter", "inbox-reconciler"}:
        raise SystemExit(f"unknown PERIMETER_ROLE: {role}")

    signal.signal(signal.SIGTERM, _handle_stop)
    signal.signal(signal.SIGINT, _handle_stop)

    while not _STOP:
        time.sleep(1)


if __name__ == "__main__":
    main()
