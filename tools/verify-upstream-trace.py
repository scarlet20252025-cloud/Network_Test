#!/usr/bin/env python3
"""Verify the opt-in relay upstream timing trace without network access."""
import asyncio
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "relay" / "ferry-socks5-server.py"
spec = importlib.util.spec_from_file_location("ferry_relay_trace_test", SOURCE)
relay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(relay)

assert relay.TRACE_UPSTREAM is False, "trace must be off by default"
assert relay.DIRECT_ATTEMPT_TIMEOUT == 1.0, "direct attempt timeout must stay at the measured 1s cap"

lines = []
relay.log = lines.append
relay._dns_cache_get = lambda host: "203.0.113.7"
relay.CLASH_PROXY = None
relay.TRACE_UPSTREAM = True

class Writer:
    def get_extra_info(self, name):
        return ("203.0.113.7", 443) if name == "peername" else None

    def close(self):
        pass

attempts = 0

async def fake_open_connection(host, port, family=None):
    global attempts
    attempts += 1
    if attempts == 1:
        raise TimeoutError()
    return object(), Writer()

async def run():
    relay.asyncio.open_connection = fake_open_connection
    reader, writer, via = await relay._open_upstream("example.test", 443, "10.0.0.1")
    assert via == "direct"
    assert writer is not None

asyncio.run(run())
text = "\n".join(lines)
checks = [
    "dns=cache",
    "direct attempt=1 fail",
    "TimeoutError",
    "direct attempt=2 ok",
]
for item in checks:
    assert item in text, f"missing trace fragment: {item}\n{text}"
assert attempts == 2, attempts
print("PASS upstream trace is opt-in and records cache, retry failure, type, and success")
