"""Python integration perimeter: dispatcher, receipt adapter, inbox reconciler."""

from __future__ import annotations

import os


def main() -> None:
    role = os.environ.get("PERIMETER_ROLE", "")
    if role == "outbox-dispatcher":
        from perimeter.dispatcher import run
    elif role == "receipt-adapter":
        from perimeter.adapter import run
    elif role == "inbox-reconciler":
        from perimeter.reconciler import run
    else:
        raise SystemExit(f"unknown PERIMETER_ROLE: {role}")
    run()


if __name__ == "__main__":
    main()
