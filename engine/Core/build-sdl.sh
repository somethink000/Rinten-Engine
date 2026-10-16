#!/bin/bash
# Builds the pinned SDL release with sdl-patches/ applied into game/bin/<platform>; "force" rebuilds it
# Needs cmake, ninja and the X11/Wayland/xkbcommon/DBus/audio dev packages SDL's README-linux lists. Keep LF endings

set -e

# The release win64 pins in engine/natives.json. One version for every platform: a window
# system bug that only reproduces on one of them is hard enough without two SDLs in the mix.
SDL_VERSION="3.4.14"
SDL_SHA256="30d4aa2b3037718142b32dffd4e72f917ebb6cc5227150e7bb9c45efb2153aeb"
SDL_URL="https://github.com/libsdl-org/SDL/releases/download/release-${SDL_VERSION}/SDL3-${SDL_VERSION}.tar.gz"

SRC="$(cd "$(dirname "$0")" && pwd)"

case "$(uname -s)" in
	Darwin) PLATFORM="osxarm64"; LIBNAME="libSDL3.0.dylib" ;;
	Linux)
		# arm64 is a Steam Frame's SteamOS - see build-linux.sh.
		case "$(uname -m)" in
			aarch64|arm64) PLATFORM="linuxsteamrtarm64" ;;
			*)             PLATFORM="linuxsteamrt64" ;;
		esac
		LIBNAME="libSDL3.so.0" ;;
	*)      echo "ERROR: build-sdl.sh runs on Linux and macOS. Windows downloads a binary." >&2; exit 1 ;;
esac

DEST="$SRC/../../game/bin/$PLATFORM"
WORK="$SRC/build/sdl/$PLATFORM"

sum() { if command -v sha256sum >/dev/null; then sha256sum | cut -d' ' -f1; else shasum -a 256 | cut -d' ' -f1; fi; }

# Which patches a build carries, so one built before a patch changed is built again
PATCHES="$(cat "$SRC"/sdl-patches/*.patch | sum)"

# Already there is the common case, and rebuilding SDL on every engine build
# would add minutes to it for nothing.
if [ "$1" != "force" ] && [ -f "$DEST/$LIBNAME" ] && [ "$(cat "$DEST/libSDL3.patches" 2>/dev/null)" = "$PATCHES" ]; then
	echo "SDL3 $SDL_VERSION already in $DEST"
	exit 0
fi

for tool in cmake ninja curl; do
	if ! command -v "$tool" >/dev/null; then
		echo "ERROR: no $tool." >&2
		exit 1
	fi
done

mkdir -p "$WORK"

ARCHIVE="$WORK/SDL3-$SDL_VERSION.tar.gz"

if [ ! -f "$ARCHIVE" ]; then
	echo "Downloading SDL $SDL_VERSION"
	curl -fsSL -o "$ARCHIVE.part" "$SDL_URL"
	mv "$ARCHIVE.part" "$ARCHIVE"
fi

# Checked every time, not only after a download: a half-written file from an
# interrupted build looks exactly like a good one to the line above.
GOT="$(sum < "$ARCHIVE")"

if [ "$GOT" != "$SDL_SHA256" ]; then
	echo "ERROR: $ARCHIVE is not the pinned release." >&2
	echo "  expected $SDL_SHA256" >&2
	echo "  got      $GOT" >&2
	rm -f "$ARCHIVE"
	exit 1
fi

UNPACKED="$WORK/SDL3-$SDL_VERSION"

if [ "$(cat "$UNPACKED/.patches" 2>/dev/null)" != "$PATCHES" ]; then
	rm -rf "$UNPACKED" "$WORK/build"
	tar -xzf "$ARCHIVE" -C "$WORK"

	for one in "$SRC"/sdl-patches/*.patch; do
		patch -d "$UNPACKED" -p1 < "$one"
	done

	echo "$PATCHES" > "$UNPACKED/.patches"
fi

# Shared only. The engine opens SDL by name at runtime through its own resolver,
# so a static SDL would be a second copy of the same code that nothing loads.
cmake -S "$UNPACKED" -B "$WORK/build" -G Ninja \
	-DCMAKE_BUILD_TYPE=Release \
	-DSDL_SHARED=ON \
	-DSDL_STATIC=OFF \
	-DSDL_TESTS=OFF \
	-DSDL_EXAMPLES=OFF \
	-DSDL_INSTALL=OFF

cmake --build "$WORK/build"

mkdir -p "$DEST"

# The real file and the names that point at it. The engine's resolver asks for "SDL3", which becomes libSDL3.so on Linux and libSDL3.dylib on
# macOS, and the soname the linker recorded is the middle one - all three have to be there or the one that is looked up is the one that is missing.
copied=0

for lib in "$WORK/build"/libSDL3.so* "$WORK/build"/libSDL3*.dylib; do
	[ -e "$lib" ] || continue

	if [ -L "$lib" ]; then
		cp -a "$lib" "$DEST/"
	else
		cp -f "$lib" "$DEST/"
	fi

	copied=$((copied + 1))
done

if [ "$copied" -eq 0 ]; then
	echo "ERROR: the build produced no libSDL3 to install." >&2
	exit 1
fi

echo "$PATCHES" > "$DEST/libSDL3.patches"

echo
echo "libSDL3 $SDL_VERSION -> $DEST"
