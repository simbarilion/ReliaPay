from __future__ import annotations

import signal
import time

import psycopg

from perimeter.config import ConfigError, ReconcilerConfig

_STOP = False


def _handle_stop(signum: int, _frame: object) -> None:
    del signum
    global _STOP
    _STOP = True


def run() -> None:
    try:
        cfg = ReconcilerConfig.from_env()
    except ConfigError as error:
        raise SystemExit(str(error)) from error
    signal.signal(signal.SIGTERM, _handle_stop)
    signal.signal(signal.SIGINT, _handle_stop)
    with psycopg.connect(autocommit=True) as connection:
        while not _STOP:
            with connection.cursor() as cursor:
                cursor.execute("SELECT delivery.reconcile_inbox(%s)", (cfg.batch_limit,))
                cursor.fetchone()
            time.sleep(cfg.poll_sec)
