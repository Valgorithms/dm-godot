"""Run the real Godot client against a controlled presentation-protocol peer.

This verifies client transport behavior, not the DM runtime or gameplay parity.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import socket
import subprocess
import threading
import unittest

CLIENT = Path(__file__).resolve().parents[1]
GODOT = os.environ.get("GODOT", "godot")


def encode(message: dict) -> bytes:
    return (json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8")


def world() -> dict:
    return {
        "type": "snapshot", "width": 3, "height": 3,
        "tiles": [
            {"x": x, "y": y, "z": 1, "density": False, "type": "/turf/floor"}
            for y in range(1, 4) for x in range(1, 4)
        ],
        "players": [{"id": "one", "name": "Fixture", "x": 2, "y": 2, "z": 1}],
    }


class ClientTransportTests(unittest.TestCase):
    def run_peer(self, handler):
        errors = []
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen(1)
            listener.settimeout(8)

            def serve():
                try:
                    with listener.accept()[0] as conn:
                        conn.settimeout(8)
                        stream = conn.makefile("rb")
                        hello = json.loads(stream.readline())
                        self.assertEqual(hello, {"type": "hello", "name": "Fixture"})
                        handler(conn, stream)
                except BaseException as exc:
                    errors.append(exc)

            thread = threading.Thread(target=serve, daemon=True)
            thread.start()
            result = subprocess.run(
                [GODOT, "--headless", "--path", str(CLIENT), "--", "--smoke-test",
                 "--name", "Fixture", "--port", str(listener.getsockname()[1])],
                text=True, capture_output=True, timeout=20,
            )
            thread.join(timeout=9)
            self.assertFalse(thread.is_alive(), "Mock server did not finish")
            if errors:
                raise errors[0]
            return result

    def test_fragmented_utf8_frames_and_authoritative_move(self):
        def handler(conn, stream):
            conn.sendall(encode({"type": "welcome", "protocol": 1, "id": "one"}))
            log = encode({"type": "log", "text": "World café ready"})
            # A multibyte character and JSON records may be split across packets.
            split = log.index("é".encode("utf-8")) + 1
            conn.sendall(log[:split])
            conn.sendall(log[split:] + encode(world()))
            move = json.loads(stream.readline())
            self.assertEqual(move, {"type": "move", "dx": 1, "dy": 0})
            state = world()
            state["players"][0]["x"] = 3
            conn.sendall(encode(state))
            stream.read()

        result = self.run_peer(handler)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('CLIENT_SMOKE_OK', result.stdout)
        self.assertIn('"x":3', result.stdout)

    def test_rejects_protocol_mismatch(self):
        def handler(conn, stream):
            conn.sendall(encode({"type": "welcome", "protocol": 99, "id": "one"}))
            stream.read()

        result = self.run_peer(handler)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Unsupported protocol", result.stderr)

    def test_rejects_snapshot_before_welcome(self):
        def handler(conn, stream):
            conn.sendall(encode(world()))
            stream.read()

        result = self.run_peer(handler)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Snapshot arrived before welcome", result.stderr)

    def test_rejects_unknown_messages(self):
        def handler(conn, stream):
            conn.sendall(encode({"type": "execute", "script": "ignored"}))
            stream.read()

        result = self.run_peer(handler)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Unknown server message", result.stderr)


if __name__ == "__main__":
    unittest.main()
