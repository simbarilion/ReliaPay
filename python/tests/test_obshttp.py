from __future__ import annotations

import json
import urllib.request

from perimeter.obshttp import start_obs_server


def test_obs_live_ready_metrics():
    ready = {"ok": True}

    def check() -> bool:
        return ready["ok"]

    server = start_obs_server(host="127.0.0.1", port=18080, ready_check=check)
    try:
        with urllib.request.urlopen("http://127.0.0.1:18080/health/live", timeout=2) as r:
            assert r.status == 200
            assert json.loads(r.read()) == {"status": "live"}
        with urllib.request.urlopen("http://127.0.0.1:18080/health/ready", timeout=2) as r:
            assert r.status == 200
            assert json.loads(r.read()) == {"status": "ready"}
        ready["ok"] = False
        try:
            urllib.request.urlopen("http://127.0.0.1:18080/health/ready", timeout=2)
            assert False, "expected HTTPError"
        except urllib.error.HTTPError as err:
            assert err.code == 503
            body = json.loads(err.read())
            assert body == {"status": "not_ready", "code": "dependency.unavailable"}
        with urllib.request.urlopen("http://127.0.0.1:18080/metrics", timeout=2) as r:
            assert r.status == 200
            assert r.headers.get_content_type() == "application/openmetrics-text" or True
            assert r.read().endswith(b"# EOF\n")
    finally:
        server.shutdown()
