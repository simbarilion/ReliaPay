from __future__ import annotations

import logging
import signal
import time

import psycopg

from perimeter.config import ConfigError, ReconcilerConfig
from perimeter import jsonlog
from perimeter.obshttp import pg_ready, start_obs_server

_STOP = False
_LOG = logging.getLogger("inbox-reconciler")


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
        obs = start_obs_server(
            host="0.0.0.0",
            port=8080,
            ready_check=lambda: pg_ready(connection),
        )
        jsonlog.info(_LOG, "reconciler started", service="inbox-reconciler", status="live")
        try:
            while not _STOP:
                with connection.cursor() as cursor:
                    cursor.execute("SELECT delivery.reconcile_inbox(%s)", (cfg.batch_limit,))
                    cursor.fetchone()
                time.sleep(cfg.poll_sec)
        finally:
            obs.shutdown()
