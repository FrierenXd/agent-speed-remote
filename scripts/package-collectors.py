#!/usr/bin/env python3
"""Stage trusted, matching collector archives for embedding in desktop distributions."""

import argparse
import hashlib
from pathlib import Path
import shutil
import tarfile


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=root / "dist")
    parser.add_argument("--output", type=Path, default=root / "dist/collectors")
    args = parser.parse_args()
    records = []
    for rid in ("linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"):
        name = f"AgentSpeed-Collector-{rid}.tar.gz"
        source = args.source / name
        if not source.is_file() or not 0 < source.stat().st_size <= 64 * 1024 * 1024:
            parser.error(f"missing/oversized {source}; build all collectors with scripts/publish-collector.sh first")
        executable = "agentspeed-collector.exe" if rid.startswith("win-") else "agentspeed-collector"
        with tarfile.open(source) as archive:
            names = set()
            for entry in archive:
                path = Path(entry.name)
                if path.is_absolute() or ".." in path.parts or not (entry.isfile() or entry.isdir()):
                    parser.error(f"unsafe archive entry: {source}: {entry.name}")
                names.add(str(path))
            if executable not in names:
                parser.error(f"collector executable missing from {source}")
        records.append((source, hashlib.sha256(source.read_bytes()).hexdigest()))
    args.output.mkdir(parents=True, exist_ok=True)
    for source, _ in records:
        target = args.output / source.name
        if source.resolve() != target.resolve():
            shutil.copyfile(source, target)
    (args.output / "SHA256SUMS.txt").write_text(
        "".join(f"{digest}  {source.name}\n" for source, digest in records), encoding="ascii"
    )
    print(f"Staged {len(records)} collector archives and SHA-256 manifest in {args.output}")


if __name__ == "__main__":
    main()
