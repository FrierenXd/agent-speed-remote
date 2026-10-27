#!/usr/bin/env bash
set -euo pipefail

# Self-contained, untrimmed builds preserve the shared transcript readers.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
targets=("${@}")
if ((${#targets[@]} == 0)); then
    targets=(linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64 win-arm64)
fi
for rid in "${targets[@]}"; do
    case "$rid" in
        linux-x64|linux-arm64|osx-x64|osx-arm64|win-x64|win-arm64) ;;
        *) echo "Unsupported runtime: $rid" >&2; exit 2 ;;
    esac
done
mkdir -p "$root/dist"
for rid in "${targets[@]}"; do
    output="$root/dist/collector/$rid"
    "$dotnet" publish "$root/windows/tools/AgentSpeed.Collector" -c Release -r "$rid" \
        --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o "$output"
    # Keep native/runtime sidecars together; extracting the archive is the installation.
    tar -czf "$root/dist/AgentSpeed-Collector-$rid.tar.gz" -C "$output" .
done
