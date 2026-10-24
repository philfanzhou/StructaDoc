"""Adapter acceptance: the real ``MinerULayoutDetectionProvider`` (.NET)
against this worker over real HTTP.

The worker is started as a subprocess with the stub engine on an ephemeral
port; a small .NET harness in ``adapter-check/`` exercises the actual adapter
type — the strict response parser that rejects unknown properties — against
it. The test is skipped when the `dotnet` SDK is not available; CI runs it
with the SDK installed.
"""

from __future__ import annotations

import json
import os
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import pytest

WORKER_ROOT = Path(__file__).resolve().parent.parent
ADAPTER_CHECK_PROJECT = WORKER_ROOT / "adapter-check"


def _free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def _wait_ready(port: int, timeout: float = 30.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(
                    f"http://127.0.0.1:{port}/health/ready", timeout=2) as response:
                if response.status == 200:
                    return
        except (urllib.error.URLError, OSError):
            time.sleep(0.2)
    raise TimeoutError("the worker did not become ready in time")


def _dotnet_available() -> bool:
    return shutil.which("dotnet") is not None


@pytest.mark.skipif(
    os.environ.get("LAYOUT_WORKER_ADAPTER_CHECK") == "0",
    reason="LAYOUT_WORKER_ADAPTER_CHECK=0 disables the adapter-acceptance check")
@pytest.mark.skipif(not _dotnet_available(), reason="the dotnet SDK is not on PATH")
def test_real_adapter_accepts_the_worker_response(tmp_path):
    port = _free_port()
    token = "adapter-check-token"
    environment = {
        **os.environ,
        "LAYOUT_WORKER_HOST": "127.0.0.1",
        "LAYOUT_WORKER_PORT": str(port),
        "LAYOUT_WORKER_ENGINE": "stub",
        "LAYOUT_WORKER_BEARER_TOKEN": token,
        "LAYOUT_WORKER_MAX_CONCURRENCY": "2",
        "LAYOUT_WORKER_MAX_QUEUE": "4",
    }
    worker = subprocess.Popen(
        [sys.executable, "-m", "structadoc_layout_worker"],
        cwd=WORKER_ROOT,
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )
    try:
        _wait_ready(port)
        completed = subprocess.run(
            [
                "dotnet", "run", "--project", str(ADAPTER_CHECK_PROJECT),
                "--", f"http://127.0.0.1:{port}", token,
            ],
            cwd=WORKER_ROOT,
            capture_output=True,
            text=True,
            timeout=300,
        )
        if completed.returncode != 0:
            pytest.fail(
                "the adapter check failed:\n"
                f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}")
        summary = json.loads(completed.stdout.strip().splitlines()[-1])
        assert summary["accepted"] is True
        assert summary["regions"] >= 1
        assert summary["engine"] == "stub"
        assert summary["width"] > 0 and summary["height"] > 0
        assert summary["wrongCredentialRejected"] is True
    finally:
        worker.terminate()
        try:
            worker.wait(timeout=15)
        except subprocess.TimeoutExpired:
            worker.kill()
            worker.wait()
