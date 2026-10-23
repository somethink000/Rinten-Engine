#!/bin/bash
# Builds FFmpeg for Android and installs it into game/bin/android-arm64/ffmpeg, for VideoPlayer on a phone.
# Nobody publishes an LGPL FFmpeg for Android, so it is built here from the release the desktop libraries are (see natives.json):
# the engine reads FFmpeg's structures at that release's offsets, and arm64 lays them out as x86-64 does.
# Decoders only - a phone plays video and records none - and LGPL: nothing GPL or nonfree is enabled.
# Needs the Android NDK, found through ANDROID_NDK_HOME (or ANDROID_NDK_ROOT), and make. Keep LF line endings.

set -e

VERSION=9.0.2
SHA256=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e
API=28

NDK="${ANDROID_NDK_HOME:-$ANDROID_NDK_ROOT}"

if [ -z "$NDK" ]; then
	echo "ERROR: no Android NDK." >&2
	echo "Set ANDROID_NDK_HOME to it - for example ~/Android/Sdk/ndk/27.0.12077973" >&2
	exit 1
fi

TC="$(ls -d "$NDK"/toolchains/llvm/prebuilt/*/ 2>/dev/null | head -1)"

if [ ! -x "$TC/bin/aarch64-linux-android$API-clang" ]; then
	echo "ERROR: no aarch64 clang for API $API in $NDK." >&2
	exit 1
fi

SRC="$(cd "$(dirname "$0")" && pwd)"
BLD="$SRC/build/android-arm64-ffmpeg"
DEST="$SRC/../../game/bin/android-arm64/ffmpeg"
ARCHIVE="$BLD/ffmpeg-$VERSION.tar.xz"

if [ "$1" = "fresh" ]; then
	rm -rf "$BLD"
fi

mkdir -p "$BLD"

if [ ! -f "$ARCHIVE" ]; then
	curl -sSfL -o "$ARCHIVE.part" "https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz"
	mv "$ARCHIVE.part" "$ARCHIVE"
fi

echo "$SHA256  $ARCHIVE" | sha256sum -c --quiet

if [ ! -d "$BLD/ffmpeg-$VERSION" ]; then
	tar -xf "$ARCHIVE" -C "$BLD"
fi

cd "$BLD/ffmpeg-$VERSION"

# What a game's video is: MP4, WebM, Matroska, Ogg and the rest, in the codecs those carry. AV1 is left out - FFmpeg decodes it
# only through dav1d, a library of its own - so an AV1 video on a phone says there is no decoder for it and plays nothing.
DECODERS=h264,hevc,vp8,vp9,mpeg4,mpeg2video,mpeg1video,theora,mjpeg,aac,aac_latm,mp3,mp3float,opus,vorbis,flac,alac,pcm_s16le,pcm_s24le,pcm_f32le,pcm_u8
DEMUXERS=mov,matroska,ogg,avi,mpegts,mpegps,m4v,h264,hevc,mp3,aac,flac,wav
PARSERS=h264,hevc,vp8,vp9,mpeg4video,mpegvideo,aac,aac_latm,mpegaudio,opus,vorbis,flac

# Flexible page sizes, as the core is: segments aligned to 16 KB, or a phone running 16 KB pages will not load the libraries.
# The names are plain lib*.so, the only names a package carries - configure's android target installs them so, soname and all.
./configure \
	--prefix="$BLD/install" \
	--target-os=android \
	--arch=aarch64 \
	--cpu=armv8-a \
	--enable-cross-compile \
	--cc="$TC/bin/aarch64-linux-android$API-clang" \
	--cxx="$TC/bin/aarch64-linux-android$API-clang++" \
	--ar="$TC/bin/llvm-ar" \
	--nm="$TC/bin/llvm-nm" \
	--ranlib="$TC/bin/llvm-ranlib" \
	--strip="$TC/bin/llvm-strip" \
	--sysroot="$TC/sysroot" \
	--extra-ldflags="-Wl,-z,max-page-size=16384" \
	--enable-shared \
	--disable-static \
	--enable-pic \
	--disable-programs \
	--disable-doc \
	--disable-debug \
	--disable-avdevice \
	--disable-avfilter \
	--disable-everything \
	--enable-decoder="$DECODERS" \
	--enable-demuxer="$DEMUXERS" \
	--enable-parser="$PARSERS" \
	--enable-protocol=file,http,tcp \
	--enable-swscale \
	--enable-swresample

make -j"$(nproc)"
make install

mkdir -p "$DEST"

for lib in avutil swresample swscale avcodec avformat; do
	cp "$BLD/install/lib/lib$lib.so" "$DEST/lib$lib.so"
	"$TC/bin/llvm-strip" --strip-unneeded "$DEST/lib$lib.so"
done

# The licence travels with the libraries, as the desktop's does
cp COPYING.LGPLv2.1 "$DEST/LICENSE.txt"

echo
echo "FFmpeg $VERSION -> $DEST ($(du -sh "$DEST" | cut -f1))"
