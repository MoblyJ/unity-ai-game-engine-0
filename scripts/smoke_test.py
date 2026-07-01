#!/usr/bin/env python3
"""Smoke-test the Unity bridge transport WITHOUT a full MCP client.

Two modes:

  # Prove the client<->bridge JSON round-trip with no Unity at all:
  python scripts/smoke_test.py --stub

  # Talk to a REAL running Unity bridge (Editor open, Agent Bridge window started):
  python scripts/smoke_test.py                    # sends scene_info
  python scripts/smoke_test.py --op find --args '{"name":"Main"}'
  python scripts/smoke_test.py --host 127.0.0.1 --port 8765

The --stub mode spins up a tiny in-process TCP server that speaks the same envelope the
C# bridge does, then drives it through server/unity_client.py — so it verifies our transport,
framing, id-correlation, and error handling with zero external dependencies.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import threading

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "server"))

from unity_client import UnityClient, UnityBridgeError  # noqa: E402


def _run_stub_server(host: str, port: int, ready: threading.Event) -> None:
    """A minimal stand-in for the C# bridge: newline JSON in, {ok,data} out."""
    import socketserver

    class Handler(socketserver.StreamRequestHandler):
        def handle(self) -> None:
            for raw in self.rfile:
                line = raw.decode("utf-8").strip()
                if not line:
                    continue
                req = json.loads(line)
                op = req.get("op")
                if op == "editor_version":
                    resp = {"id": req["id"], "ok": True,
                            "data": {"unity_version": "6000.0.23f1", "render_pipeline_family": "URP",
                                     "supported_by_bridge": True}}
                elif op == "scene_info":
                    data = {"scene": {"name": "StubScene", "dirty": False, "root_count": 1},
                            "hierarchy": [{"id": 1, "name": "Main Camera", "path": "Main Camera"}]}
                    resp = {"id": req["id"], "ok": True, "data": data}
                elif op == "create_object":
                    resp = {"id": req["id"], "ok": True,
                            "data": {"id": 42, "name": req["args"].get("name", "GameObject"), "path": "GameObject"}}
                elif op == "boom":
                    resp = {"id": req["id"], "ok": False, "error": "Unknown op 'boom'."}
                else:
                    resp = {"id": req["id"], "ok": True, "data": {"echo": req.get("args", {})}}
                self.wfile.write((json.dumps(resp) + "\n").encode("utf-8"))
                self.wfile.flush()

    class Server(socketserver.ThreadingTCPServer):
        allow_reuse_address = True
        daemon_threads = True

    with Server((host, port), Handler) as srv:
        ready.set()
        srv.serve_forever()


async def _drive(host: str, port: int, op: str, args: dict) -> int:
    client = UnityClient(host=host, port=port, timeout=5.0)
    try:
        data = await client.request(op, args)
        print(f"OK  {op} ->")
        print(json.dumps(data, indent=2))
    except UnityBridgeError as exc:
        print(f"ERR {op} -> {exc}")
        return 1
    return 0


async def _stub_flow(host: str, port: int) -> int:
    client = UnityClient(host=host, port=port, timeout=5.0)
    # 1) happy path
    info = await client.request("scene_info", {"include_hierarchy": True})
    assert info["scene"]["name"] == "StubScene", info
    # 2) authoring round-trip preserves args
    created = await client.request("create_object", {"primitive": "cube", "name": "Hero"})
    assert created["name"] == "Hero", created
    # 3) structured error surfaces as UnityBridgeError
    try:
        await client.request("boom", {})
    except UnityBridgeError as exc:
        assert "boom" in str(exc), exc
    else:
        print("FAIL: expected an error for op 'boom'")
        return 1
    print("PASS: stub round-trip (scene_info, create_object echo, error handling) all correct.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--stub", action="store_true", help="Run against an in-process stub bridge (no Unity).")
    parser.add_argument("--host", default=os.environ.get("UNITY_BRIDGE_HOST", "127.0.0.1"))
    parser.add_argument("--port", type=int, default=int(os.environ.get("UNITY_BRIDGE_PORT", "8765")))
    parser.add_argument("--op", default="scene_info", help="Op to send in real mode.")
    parser.add_argument("--args", default="{}", help="JSON args for --op in real mode.")
    ns = parser.parse_args()

    if ns.stub:
        stub_port = 8799
        ready = threading.Event()
        t = threading.Thread(target=_run_stub_server, args=(ns.host, stub_port, ready), daemon=True)
        t.start()
        ready.wait(timeout=5)
        return asyncio.run(_stub_flow(ns.host, stub_port))

    return asyncio.run(_drive(ns.host, ns.port, ns.op, json.loads(ns.args)))


if __name__ == "__main__":
    raise SystemExit(main())
