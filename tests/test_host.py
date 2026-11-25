"""Regression tests against the real C# diagnostic host (Release build required)."""
from __future__ import annotations

import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from check_end_to_end import DEFAULT_PROJECT, HOST_DLL, ProtocolClient, RunningHost, default_dotnet


def position(snapshot: dict, identity: str) -> tuple[int, int, int]:
    player = next(value for value in snapshot["players"] if str(value["id"]) == identity)
    return player["x"], player["y"], player["z"]


class HostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not HOST_DLL.exists() or not DEFAULT_PROJECT.exists():
            raise RuntimeError("Compile fixtures/smoke and build the Release host before running host tests")

    def setUp(self):
        self.host = RunningHost(default_dotnet(), DEFAULT_PROJECT)
        self.addCleanup(self.host.close)

    def connect(self) -> ProtocolClient:
        client = ProtocolClient(self.host.port)
        self.addCleanup(client.close)
        return client

    def assert_grid_snapshot(self, state, players):
        self.assertEqual(set(state), {"type", "width", "height", "revision", "tiles", "players"})
        self.assertEqual((state["width"], state["height"]), (5, 5))
        tiles = []
        for y in range(1, 6):
            for x in range(1, 6):
                wall = x in (1, 5) or y in (1, 5)
                tiles.append({"x": x, "y": y, "z": 1, "density": wall, "type": "/turf/wall" if wall else "/turf/floor"})
        self.assertCountEqual(state["tiles"], tiles)
        self.assertCountEqual(state["players"], [
            {"id": peer.id, "name": name, "x": x, "y": y, "z": 1} for peer, name, x, y in players
        ])

    def test_two_clients_occupancy_broadcast_and_disconnect(self):
        project = json.loads(DEFAULT_PROJECT.read_text())
        hidden = project["Maps"][0]["CellDefinitions"]["c"]["Objects"][0]
        self.assertEqual(project["Types"][hidden["Type"]]["Path"], "/mob/player")
        self.assertEqual(hidden["VarOverrides"]["invisibility"], 100)
        self.assertEqual(hidden["VarOverrides"]["desc"], "<i>survey marker</i>")
        self.assertEqual(hidden["VarOverrides"]["dir"], 8)
        first = self.connect()
        first_state = first.join("First")
        self.assertEqual(position(first_state, first.id), (2, 2, 1))
        second = self.connect()
        second_state = second.join("Second")
        self.assertEqual(position(second_state, second.id), (3, 2, 1))
        both = first.snapshot(lambda state: len(state["players"]) == 2)
        for state in (second_state, both):
            self.assert_grid_snapshot(state, [(first, "First", 2, 2), (second, "Second", 3, 2)])
        # An occupied tile is blocked, with an unchanged authoritative snapshot.
        first.send({"type": "move", "dx": 1, "dy": 0})
        self.assertEqual(position(first.snapshot(), first.id), (2, 2, 1))
        second.send({"type": "move", "dx": 0, "dy": 1})
        moved = first.snapshot(lambda state: position(state, second.id) == (3, 3, 1))
        self.assertGreater(moved["revision"], both["revision"])
        # The diagnostic marker can enter the mapped mob's tile. Neither viewer receives that mob.
        second_moved = second.snapshot(lambda state: position(state, second.id) == (3, 3, 1))
        for state in (moved, second_moved):
            self.assert_grid_snapshot(state, [(first, "First", 2, 2), (second, "Second", 3, 3)])
        first.send({"type": "move", "dx": 1, "dy": 0})
        self.assertEqual(position(first.snapshot(), first.id), (3, 2, 1))
        second.close()
        remaining = first.snapshot(lambda state: len(state["players"]) == 1)
        self.assertEqual(remaining["players"][0]["id"], first.id)

    def test_wall_blocks_and_valid_move_remains_available(self):
        client = self.connect()
        client.join("Wall check")
        client.send({"type": "move", "dx": -1, "dy": 0})
        self.assertEqual(position(client.snapshot(), client.id), (2, 2, 1))
        client.send({"type": "move", "dx": 0, "dy": 1})
        self.assertEqual(position(client.snapshot(), client.id), (2, 3, 1))

    def test_invalid_commands_cannot_crash_host_or_move_player(self):
        client = self.connect()
        client.send({"type": "move", "dx": 1, "dy": 0})
        self.assertIn("hello", client.receive(lambda value: value["type"] == "error")["text"])
        client.join("Validation")
        for dx, dy in [(0, 0), (1, 1), (2, 0), (1.5, 0), ("1", 0), (None, 0), (True, 0), ({}, 0), ([], 0), (2**63, 0)]:
            with self.subTest(dx=dx, dy=dy):
                client.send({"type": "move", "dx": dx, "dy": dy})
                error = client.receive(lambda value: value["type"] == "error")
                self.assertIn("cardinal integer", error["text"])
                self.assertIsNone(self.host.process.poll())
        client.send({"type": "hello", "name": "duplicate"})
        self.assertIn("Already joined", client.receive(lambda value: value["type"] == "error")["text"])
        client.send({"type": "arbitrary"})
        self.assertIn("Unsupported", client.receive(lambda value: value["type"] == "error")["text"])
        client.send({"type": "move", "dx": 1, "dy": 0})
        self.assertEqual(position(client.snapshot(), client.id), (3, 2, 1))

    def test_malformed_json_and_invalid_names_recover(self):
        client = self.connect()
        client.socket.sendall(b"{\n")
        self.assertIn("Malformed", client.receive(lambda value: value["type"] == "error")["text"])
        for name in ("", " ", "x" * 41, None, 1):
            client.send({"type": "hello", "name": name})
            self.assertIn("name", client.receive(lambda value: value["type"] == "error")["text"])
        state = client.join("Recovered")
        self.assertEqual(len(state["players"]), 1)


if __name__ == "__main__":
    unittest.main()
