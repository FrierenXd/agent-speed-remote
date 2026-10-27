set -eu
case "$(uname -s)" in
    Linux) os=linux ;;
    Darwin) os=osx ;;
    *) echo 'Unsupported remote operating system' >&2; exit 42 ;;
esac
case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    arm64|aarch64) arch=arm64 ;;
    *) echo 'Unsupported remote CPU architecture' >&2; exit 42 ;;
esac
if [ "$os" = linux ] && { ls /lib/ld-musl-*.so.1 >/dev/null 2>&1 || ldd --version 2>&1 | grep -qi musl; }; then
    echo 'Alpine/musl is not supported; a glibc host is required' >&2
    exit 42
fi
printf 'AGENTSPEED_DEPLOY_V1\n%s-%s\n' "$os" "$arch"
