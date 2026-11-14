#!/usr/bin/env python3
"""Exercise compiled DM -> C# runtime/host -> two real Godot clients.

Movement here is the host's explicit diagnostic grid, not DM mob behavior.
"""
from __future__ import annotations

import argparse
from contextlib import AbstractContextManager
import json
import os
from pathlib import Path
from queue import Empty, Queue
import shutil
import socket
import subprocess
import sys
from threading import Thread
import time

ROOT = Path(__file__).resolve().parents[1]
HOST_DLL = ROOT / "src/DmGodot.Host/bin/Release/net10.0/DmGodot.Host.dll"
DEFAULT_PROJECT = ROOT / "build/smoke.json"


def default_dotnet() -> str:
    local = ROOT.parent / ".tools/dotnet/dotnet"
    return os.environ.get("DM_GODOT_DOTNET", os.environ.get("DOTNET", str(local) if local.exists() else "dotnet"))


def default_godot() -> str:
    local = ROOT.parent / ".tools/godot/Godot_v4.7.2-stable_linux.x86_64"
    return os.environ.get("GODOT", str(local) if local.exists() else "godot")


class RunningHost(AbstractContextManager):
    """A real loopback host with an OS-assigned port and captured diagnostics."""

    def __init__(self, dotnet: str, project: Path, duration: int = 30):
        self.messages: list[dict] = []
        self.output: list[str] = []
        self._lines: Queue[str] = Queue()
        self.port = 0
        self.process = subprocess.Popen(
            [dotnet, str(HOST_DLL), "--project", str(project), "--diagnostic-grid",
             "--port", "0", "--duration", str(duration)],
            cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        )
        self._reader = Thread(target=self._read, daemon=True)
        self._reader.start()
        try:
            deadline = time.monotonic() + 12
            while time.monotonic() < deadline:
                try:
                    line = self._lines.get(timeout=0.1)
                except Empty:
                    if self.process.poll() is not None:
                        raise RuntimeError("Host exited before listening:\n" + "".join(self.output))
                    continue
                try:
                    message = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if message.get("type") == "listening":
                    self.port = message["port"]
                    break
            if not self.port:
                raise TimeoutError("Host did not announce a listening port")
        except BaseException:
            self.close()
            raise

    def _read(self):
        assert self.process.stdout is not None
        for line in self.process.stdout:
            self.output.append(line)
            self._lines.put(line)
            try:
                value = json.loads(line)
                if isinstance(value, dict):
                    self.messages.append(value)
            except json.JSONDecodeError:
                pass

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        self._reader.join(timeout=2)
        if self.process.stdout is not None:
            self.process.stdout.close()

    def __exit__(self, exc_type, exc, tb):
        self.close()


class ProtocolClient(AbstractContextManager):
    def __init__(self, port: int):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=5)
        self.socket.settimeout(5)
        self.stream = self.socket.makefile("rb")
        self.id = ""

    def send(self, message: dict):
        self.socket.sendall((json.dumps(message) + "\n").encode())

    def receive(self, predicate=lambda value: True) -> dict:
        for _ in range(100):
            line = self.stream.readline()
            if not line:
                raise ConnectionError("Host closed the connection")
            message = json.loads(line)
            if predicate(message):
                return message
        raise AssertionError("Expected protocol message was not received")

    def join(self, name: str) -> dict:
        self.send({"type": "hello", "name": name})
        welcome = self.receive(lambda value: value["type"] == "welcome")
        if welcome["protocol"] != 1:
            raise AssertionError("Unexpected protocol version")
        self.id = str(welcome["id"])
        return self.snapshot()

    def snapshot(self, predicate=lambda value: True) -> dict:
        return self.receive(lambda value: value["type"] == "snapshot" and predicate(value))

    def close(self):
        try:
            self.socket.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.stream.close()
        self.socket.close()

    def __exit__(self, exc_type, exc, tb):
        self.close()


def run_clients(dotnet: str, godot: str, project: Path) -> dict:
    if not HOST_DLL.exists():
        raise FileNotFoundError(f"Build the Release host first: {HOST_DLL}")
    if not project.exists():
        raise FileNotFoundError(f"Compile the DM fixture first: {project}")
    if not Path(godot).exists() and shutil.which(godot) is None:
        raise FileNotFoundError(f"Godot binary not found: {godot}")
    clients = []
    results = []
    with RunningHost(dotnet, project) as host:
        try:
            for name in ("Godot-A", "Godot-B"):
                clients.append(subprocess.Popen(
                    [godot, "--headless", "--path", str(ROOT / "client"), "--",
                     "--port", str(host.port), "--name", name,
                     "--smoke-test", "--smoke-min-players", "2"],
                    cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                ))
            for client in clients:
                stdout, stderr = client.communicate(timeout=22)
                if client.returncode != 0:
                    raise RuntimeError(f"Godot smoke failed ({client.returncode}):\n{stdout}\n{stderr}")
                records = [line.removeprefix("CLIENT_SMOKE_OK ") for line in stdout.splitlines()
                           if line.startswith("CLIENT_SMOKE_OK ")]
                if len(records) != 1:
                    raise AssertionError("Missing unique client success record:\n" + stdout)
                results.append(json.loads(records[0]))
            ids = {result["id"] for result in results}
            if len(ids) != 2:
                raise AssertionError("Godot clients did not receive distinct IDs")
            for result in results:
                if not ids.issubset(set(result["observed_players"])):
                    raise AssertionError("Both clients must observe each other before moving")
            return {
                "status": "passed", "protocol": 1, "mode": "diagnostic-grid",
                "godot": subprocess.check_output([godot, "--version"], text=True).strip(),
                "project": str(project.relative_to(ROOT)) if project.is_relative_to(ROOT) else str(project),
                "clients": results,
                "dm_logs": [value for value in host.messages if value.get("type") == "dm-log"],
                "scope": "Two real Godot clients see each other and receive authoritative movement confirmations. DM mob lifecycle is not implemented.",
            }
        finally:
            for client in clients:
                if client.poll() is None:
                    client.kill()
                    client.communicate(timeout=5)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=default_dotnet())
    parser.add_argument("--godot", default=default_godot())
    parser.add_argument("--project", type=Path, default=DEFAULT_PROJECT)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        result = run_clients(args.dotnet, args.godot, args.project.resolve())
    except (AssertionError, OSError, RuntimeError, subprocess.SubprocessError) as exc:
        print(f"END_TO_END_FAILED: {exc}", file=sys.stderr)
        return 1
    rendered = json.dumps(result, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered)
    print(rendered, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
