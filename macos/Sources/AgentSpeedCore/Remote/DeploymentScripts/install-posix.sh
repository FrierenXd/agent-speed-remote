set -eu
umask 077
hash='__SHA256__'
case "$HOME" in /*) ;; *) echo 'An absolute HOME is required' >&2; exit 2 ;; esac
root="$HOME/.local/lib/agentspeed-collector/releases"
mkdir -p "$root"
stage=$(mktemp -d "$root/.upload.XXXXXXXX")
locked=false
cleanup() {
    rm -rf "$stage"
    if [ "$locked" = true ]; then rmdir "$root/.lock-$hash"; fi
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM
cat > "$stage/payload.tar.gz"
if command -v sha256sum >/dev/null 2>&1; then
    actual=$(sha256sum "$stage/payload.tar.gz" | cut -d ' ' -f 1)
elif command -v shasum >/dev/null 2>&1; then
    actual=$(shasum -a 256 "$stage/payload.tar.gz" | cut -d ' ' -f 1)
else
    echo 'sha256sum or shasum is required' >&2; exit 2
fi
[ "$actual" = "$hash" ] || { echo 'Collector SHA-256 mismatch' >&2; exit 3; }
mkdir "$stage/runtime"
tar -xzf "$stage/payload.tar.gz" -C "$stage/runtime"
chmod 700 "$stage/runtime/agentspeed-collector"
[ "$("$stage/runtime/agentspeed-collector" --check)" = AGENTSPEED_COLLECTOR_V1 ] || exit 4
printf '%s' "$hash" > "$stage/runtime/.agentspeed-sha256"
mkdir "$root/.lock-$hash" || { echo 'Another deployment is active; retry shortly' >&2; exit 5; }
locked=true
final="$root/$hash"
if [ -e "$final" ]; then
    [ "$(cat "$final/.agentspeed-sha256")" = "$hash" ] || exit 3
    [ "$("$final/agentspeed-collector" --check)" = AGENTSPEED_COLLECTOR_V1 ] || exit 4
else
    mv "$stage/runtime" "$final"
fi
printf 'AGENTSPEED_INSTALLED_V1\n%s/agentspeed-collector\n' "$final"
