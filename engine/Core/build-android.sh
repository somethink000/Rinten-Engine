#!/bin/bash
# Builds rintencore.so for Android and installs it into game/bin/android-arm64. ./build-android.sh            incremental ./build-android.sh fresh      delete the build dir first The same build build-android.cmd does on Windows, and the same one core.yml runs on ubuntu. It existed in those two places and in neither of them could a person on Linux or a Mac run it: the script was Windows-only and the workflow is a yaml. Now the workflow calls this. Needs the Android NDK, found by find-ndk.sh (ANDROID_NDK_HOME, or the Android SDK).
# Everything else comes with it: the NDK ships its own CMake toolchain file and a clang that targets the phone. arm64 only. A 32-bit phone will not run this engine, and every device Vulkan is worth having on has been arm64 for years. Note that a phone cannot compile a shader: Slang has no Android build, so an Android game runs pre-baked .shader_c the way the web does. This script builds the core; it does not make that problem go away. Keep LF line endings or Linux will not execute this.

set -e

. "$(dirname "$0")/find-ndk.sh"

TOOLCHAIN="$NDK/build/cmake/android.toolchain.cmake"

if [ ! -f "$TOOLCHAIN" ]; then
	echo "ERROR: $TOOLCHAIN is not there, so $NDK is not an NDK." >&2
	exit 1
fi

for tool in cmake ninja; do
	if ! command -v "$tool" >/dev/null; then
		echo "ERROR: no $tool on the path." >&2
		exit 1
	fi
done

SRC="$(cd "$(dirname "$0")" && pwd)"
BLD="$SRC/build/android-arm64"
DEST="$SRC/../../game/bin/android-arm64"

if [ "$1" = "fresh" ]; then
	rm -rf "$BLD"
fi

# API 28 is where Vulkan is worth relying on and where the NDK's own default sits; below that the loader is present on paper and absent in practice. Flexible page sizes: the library's segments aligned to 16 KB
# rather than 4. Phones from Android 15 on may run with 16 KB pages, and there a library laid out for 4 KB does not load at all - the store refuses a package carrying one. Aligned for 16 it loads on either.
cmake -S "$SRC" -B "$BLD" -G Ninja \
	-DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
	-DANDROID_ABI=arm64-v8a \
	-DANDROID_PLATFORM=android-28 \
	-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON \
	-DCMAKE_BUILD_TYPE=Release

cmake --build "$BLD"

mkdir -p "$DEST"
cp "$BLD/bin/rintencore.so" "$DEST/rintencore.so"

# Strip what goes in the apk. The NDK's Release build keeps full debug info in the library - 25 MB of it against 2.8 MB for the same core on Linux - and every one of those megabytes is downloaded by every player. Nothing on a phone reads
# them: a crash is symbolised against the build, not against what is on the device. So the copy that ships is stripped and the one in build/ is not, which is the copy a stack trace gets symbolised with. Do not delete build/ before doing that.
STRIP="$(ls "$NDK"/toolchains/llvm/prebuilt/*/bin/llvm-strip 2>/dev/null | head -1)"

if [ -x "$STRIP" ]; then
	before=$(stat -c %s "$DEST/rintencore.so" 2>/dev/null || echo 0)
	"$STRIP" --strip-unneeded "$DEST/rintencore.so"
	after=$(stat -c %s "$DEST/rintencore.so" 2>/dev/null || echo 0)
	echo "stripped: $((before / 1048576)) MB -> $((after / 1048576)) MB (symbols stay in $BLD/bin)"
else
	echo "WARNING: no llvm-strip in $NDK - shipping rintencore.so with its debug info," >&2
	echo "which is about twenty megabytes of apk nobody reads." >&2
fi

echo
echo "rintencore.so -> $DEST"
