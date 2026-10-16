#!/bin/bash
# Builds rintencore.so and installs it into game/bin/linuxsteamrt64 - or game/bin/linuxsteamrtarm64 on an arm64 machine (a Steam Frame's SteamOS; see the linux-arm64 job in .github/workflows/core.yml, which is where that one is built, since nothing here cross-compiles). ./build-linux.sh            incremental ./build-linux.sh fresh      delete the build
# dir first Needs cmake, ninja and a C++20 compiler. For a build that can open a window it also needs the X11 or Wayland headers - without them everything else still builds and CMake says so, which is what a dedicated server wants anyway. sudo apt install cmake ninja-build clang libx11-dev libwayland-dev Keep LF line endings or Linux will not execute this.

set -e

SRC="$(cd "$(dirname "$0")" && pwd)"
# What the engine calls this machine's folder - LauncherEnvironment.PlatformName
# spells it the same way from the process's architecture.
case "$(uname -m)" in
	aarch64|arm64) PLATFORM="linuxsteamrtarm64" ;;
	*)             PLATFORM="linuxsteamrt64" ;;
esac

BLD="$SRC/build/$PLATFORM"
DEST="$SRC/../../game/bin/$PLATFORM"

for tool in cmake ninja; do
	if ! command -v "$tool" >/dev/null; then
		echo "ERROR: no $tool. Try: sudo apt install cmake ninja-build clang" >&2
		exit 1
	fi
done

if [ "$1" = "fresh" ]; then
	rm -rf "$BLD"
fi

# SDL, which the engine opens by name at runtime. There is no binary release for this platform, so it is built
# from the pinned source once and left in place - see build-sdl.sh, which builds it again only when its patches change.
"$SRC/build-sdl.sh"

# And the OpenXR loader, the same way and for the same reason - see
# build-openxr.sh.
"$SRC/build-openxr.sh"

# And NVIDIA's NGX, which DLSS is reached through - linked here from NVIDIA's static library, and never committed;
# see build-ngx.sh. A machine that cannot fetch it still builds: the engine then simply offers no DLSS.
"$SRC/build-ngx.sh" || echo "NGX not built - DLSS will not be offered"

cmake -S "$SRC" -B "$BLD" -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build "$BLD"

mkdir -p "$DEST"
cp "$BLD/bin/rintencore.so" "$DEST/rintencore.so"

echo
echo "rintencore.so -> $DEST"
