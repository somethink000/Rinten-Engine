#!/bin/bash
# Builds rintencore.dylib and installs it into game/bin/osxarm64. ./build-macos.sh            incremental ./build-macos.sh fresh      delete the build dir first Needs cmake, ninja and the Xcode command line tools (xcode-select --install). brew install cmake ninja Apple Silicon only. There is no Intel Mac this engine is worth running on, and a universal binary would double the size of the one file we ship
# per platform. Vulkan on a Mac is Vulkan over Metal. The surface is made from a CAMetalLayer (VK_USE_PLATFORM_METAL_EXT, see CMakeLists.txt) and something has to be there to answer for it at runtime - either MoltenVK or Mesa's KosmicKrisp, which is what game/bin/osxarm64/libvulkan_kosmickrisp.dylib is. Nothing about this build picks one; the loader does. Keep LF line endings or macOS will not execute this.

set -e

SRC="$(cd "$(dirname "$0")" && pwd)"
BLD="$SRC/build/osxarm64"
DEST="$SRC/../../game/bin/osxarm64"

for tool in cmake ninja; do
	if ! command -v "$tool" >/dev/null; then
		echo "ERROR: no $tool. Try: brew install cmake ninja" >&2
		exit 1
	fi
done

if [ "$1" = "fresh" ]; then
	rm -rf "$BLD"
fi

# SDL, which the engine opens by name at runtime. There is no binary release for this platform, so it is built
# from the pinned source once and left in place - see build-sdl.sh, which builds it again only when its patches change.
"$SRC/build-sdl.sh"

cmake -S "$SRC" -B "$BLD" -G Ninja \
	-DCMAKE_BUILD_TYPE=Release \
	-DCMAKE_OSX_ARCHITECTURES=arm64

cmake --build "$BLD"

mkdir -p "$DEST"

# CMake names a macOS shared library .dylib, and PREFIX is cleared in CMakeLists so there is no lib in front of it. That is exactly the name the managed
# loader asks for - DLLImportResolver.FileNameFor turns "rintencore" into "rintencore.dylib" here, without the lib the platform would normally add.
cp "$BLD/bin/rintencore.dylib" "$DEST/rintencore.dylib"

echo
echo "rintencore.dylib -> $DEST"
