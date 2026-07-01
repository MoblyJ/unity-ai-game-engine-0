"""Async TCP client to the Unity Editor bridge.

One request -> one response, correlated by a uuid `id`, over newline-delimited JSON on
127.0.0.1:{PORT}. Because the MCP server runs on Windows next to Unity, plain localhost
works with no WSL networking setup. Connections are opened lazily and re-established on
failure so the bridge can be stopped/started (Play Mode toggles, domain reloads) without
killing the MCP server.
"""
from __future__ import annotations

import asyncio
import json
import os
import uuid
from typing import Any

DEFAULT_HOST = os.environ.get("UNITY_BRIDGE_HOST", "127.0.0.1")
DEFAULT_PORT = int(os.environ.get("UNITY_BRIDGE_PORT", "8765"))
DEFAULT_TIMEOUT = float(os.environ.get("UNITY_BRIDGE_TIMEOUT", "5.0"))


class UnityBridgeError(RuntimeError):
    """Raised when the bridge is unreachable or returns a transport-level failure."""


class UnityClient:
    def __init__(
        self,
        host: str = DEFAULT_HOST,
        port: int = DEFAULT_PORT,
        timeout: float = DEFAULT_TIMEOUT,
    ) -> None:
        self.host = host
        self.port = port
        self.timeout = timeout
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        # Serialize access: the bridge answers one request at a time on the socket.
        self._lock = asyncio.Lock()

    async def _ensure_connection(self) -> None:
        if self._writer is not None and not self._writer.is_closing():
            return
        try:
            self._reader, self._writer = await asyncio.wait_for(
                asyncio.open_connection(self.host, self.port), timeout=self.timeout
            )
        except (OSError, asyncio.TimeoutError) as exc:
            self._reader = self._writer = None
            raise UnityBridgeError(
                f"Cannot reach the Unity bridge at {self.host}:{self.port}. "
                "Is Unity open with the Agent Bridge window started? "
                f"({type(exc).__name__}: {exc})"
            ) from exc

    async def _close(self) -> None:
        if self._writer is not None:
            try:
                self._writer.close()
                await self._writer.wait_closed()
            except OSError:
                pass
        self._reader = self._writer = None

    async def request(self, op: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
        """Send one op and return the bridge's `data` payload, raising on `ok:false`."""
        payload = {"id": uuid.uuid4().hex, "op": op, "args": args or {}}
        line = (json.dumps(payload) + "\n").encode("utf-8")

        async with self._lock:
            # One transparent reconnect: the bridge socket often dies on a domain reload.
            for attempt in (1, 2):
                try:
                    await self._ensure_connection()
                    assert self._writer is not None and self._reader is not None
                    self._writer.write(line)
                    await asyncio.wait_for(self._writer.drain(), timeout=self.timeout)
                    raw = await asyncio.wait_for(
                        self._reader.readline(), timeout=self.timeout
                    )
                    break
                except (OSError, asyncio.TimeoutError, AssertionError) as exc:
                    await self._close()
                    if attempt == 2:
                        raise UnityBridgeError(
                            f"Bridge request '{op}' failed after reconnect: "
                            f"{type(exc).__name__}: {exc}"
                        ) from exc

        if not raw:
            await self._close()
            raise UnityBridgeError(
                f"Bridge closed the connection while answering '{op}' (empty response)."
            )

        try:
            response = json.loads(raw.decode("utf-8"))
        except json.JSONDecodeError as exc:
            raise UnityBridgeError(f"Bridge returned invalid JSON for '{op}': {exc}") from exc

        if not response.get("ok", False):
            raise UnityBridgeError(response.get("error", f"Unknown bridge error for '{op}'."))
        return response.get("data", {})


# Module-level singleton so every tool shares one connection.
_client: UnityClient | None = None


def get_client() -> UnityClient:
    global _client
    if _client is None:
        _client = UnityClient()
    return _client
