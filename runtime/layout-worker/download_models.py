#!/usr/bin/env python3
"""Download and verify the llama.cpp model files the layout worker needs.

Downloads the two GGUF files (the VLM weights and its multimodal projector)
from HuggingFace or ModelScope into a target directory and verifies each
file's SHA-256 against the pinned values recorded in the deployment guide
(docs/deployment/layout-runtime.md). The script is standard library only and
never writes anywhere besides the target directory.

Model weights are third-party downloads, multi-gigabyte in size; this
repository never commits them. The pinned checksums below were recorded from
the published files on 2026-10-24; if the upstream repository replaces a
file, the checksum here fails and the guide must be updated deliberately.
"""

from __future__ import annotations

import argparse
import hashlib
import sys
import urllib.request
from pathlib import Path

MODEL_FILE = "MinerU2.5-Pro-2605-1.2B-Q8_0.gguf"
MMPROJ_FILE = "mmproj-MinerU2.5-Pro-2605-1.2B-Q8_0.gguf"

HUGGINGFACE_REPO = "jinzhenj/MinerU2.5-Pro-2605-1.2B-GGUF"
MODELSCOPE_REPO = "jinzhenj/MinerU2.5-Pro-2605-1.2B-GGUF"

# SHA-256 of each published file, recorded 2026-10-24. Filled by the
# deployment guide's recording procedure; a mismatch aborts the download.
PINNED_SHA256: dict[str, str] = {
    MODEL_FILE: "3f3523429c880d675c0330cb3de78240452bc40a961d037e24f91ab8488f03e7",
    MMPROJ_FILE: "b6c08ab50352677f7396af45ee60682f353f55488e26911d8154bfc37514d3f8",
}

SOURCES = {
    "huggingface": "https://huggingface.co/{repo}/resolve/main/{file}",
    "modelscope": "https://modelscope.cn/models/{repo}/resolve/master/{file}",
}

CHUNK = 1024 * 1024


def download(source: str, repo: str, file_name: str, destination: Path) -> None:
    url = SOURCES[source].format(repo=repo, file=file_name)
    print(f"downloading {url}")
    digest = hashlib.sha256()
    partial = destination.with_suffix(destination.suffix + ".part")
    with urllib.request.urlopen(url, timeout=60) as response, partial.open("wb") as out:
        while True:
            chunk = response.read(CHUNK)
            if not chunk:
                break
            out.write(chunk)
            digest.update(chunk)
    actual = digest.hexdigest()
    expected = PINNED_SHA256.get(file_name)
    if expected is None or expected.startswith("__"):
        partial.rename(destination)
        print(f"WARNING: no pinned checksum for {file_name}; recorded sha256 is {actual}")
        return
    if actual != expected:
        partial.unlink(missing_ok=True)
        sys.exit(
            f"checksum mismatch for {file_name}: expected {expected}, got {actual}; "
            f"the upstream file may have changed — update the pinned checksums "
            f"deliberately, in the deployment guide and here together."
        )
    partial.rename(destination)
    print(f"verified {file_name} (sha256 {actual})")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source", choices=sorted(SOURCES), default="huggingface",
        help="where to download from (default: huggingface)")
    parser.add_argument(
        "--target", type=Path, default=Path("models"),
        help="directory to place the files in (default: ./models)")
    args = parser.parse_args()

    args.target.mkdir(parents=True, exist_ok=True)
    repo = HUGGINGFACE_REPO if args.source == "huggingface" else MODELSCOPE_REPO
    for file_name in (MODEL_FILE, MMPROJ_FILE):
        destination = args.target / file_name
        if destination.exists():
            print(f"{file_name} already present, skipping (delete it to re-download)")
            continue
        download(args.source, repo, file_name, destination)
    print(f"done; point LAYOUT_WORKER_MODEL_PATH and LAYOUT_WORKER_MMPROJ_PATH at the files in {args.target}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
