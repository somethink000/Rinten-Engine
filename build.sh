#!/bin/bash
# Builds the engine on Linux without re-bootstrapping. ./build.sh            this machine's core + managed engine + shaders ./build.sh linux      only rintencore.so for this machine  (native: the old name) ./build.sh macos      only rintencore.dylib ./build.sh web        only the wasm core and the page around it ./build.sh android    only rintencore.so for a phone       (needs the NDK) ./build.sh android-ffmpeg  FFmpeg for a phone's video     (needs the NDK) ./build.sh natives    every core this machine can build ./build.sh managed    only the .NET engine ./build.sh shaders    only the shader step Add --steam to any of them (before or after the
# target) and a build that succeeds is staged and pushed to the Steam depot. It needs steamcmd, logged in once by hand, and these in the environment: RINTEN_STEAM_USER     the Steam build account RINTEN_STEAM_BRANCH   branch to set the build live on (optional: left off, the build is uploaded and set live from the Builds page) The four core scripts each install into game/bin/<platform>, so a game exported for another desktop is that folder copied next to the assets. Which is why they are worth being able to run from one machine. Keep LF line endings or Linux will not execute this.

set -e
cd "$(dirname "$0")"

# --steam is a flag, not a target: take it out so the target is still $1.
steam=0
args=()
for arg in "$@"; do
	if [ "$arg" = "--steam" ]; then steam=1; else args+=("$arg"); fi
done
set -- "${args[@]}"

# No MSBuild worker nodes left behind. By default each build leaves about fourteen waiting to be reused, at some 170 MB each, and on a machine building several times an hour they pile
# up: 76 of them held 12 GB on 2026-09-24, and a build that ran out of memory beside them took the desktop down. A build still uses its workers while it runs; they just do not outlive it.
export MSBUILDDISABLENODEREUSE=1

# Never build over a running editor. The build writes game/bin/managed/*.dll and game/Rinten straight over the files a running one has mapped, and the loader in that process then segfaults on the next assembly it goes to touch. On Wayland that takes the whole desktop session with it - mutter does not survive a client dying
# that way - so the cost of forgetting is every window the user had open. Only one run from this checkout: a copy of the tree elsewhere (a bench's, an export's) maps its own files, and refusing over it held every build here back for as long as somebody else measured. A process whose binary cannot be read is counted as ours.
live=""
for pid in $(pgrep -x 'Rinten|Rinten-Standalo|Rinten-Server'); do
	exe=$(readlink "/proc/$pid/exe" 2>/dev/null) || exe=""
	case "$exe" in
		"$PWD"/*|"") live="$live $pid" ;;
	esac
done
if [ -n "$live" ]; then
	echo "The editor is running. Close it before building - a build over a live" >&2
	echo "process crashes it in the dynamic loader and can take the session down." >&2
	for pid in $live; do ps -o pid=,args= -p "$pid" >&2 || true; done
	exit 1
fi

# And the other half of the same door. The check above is one moment, and a build is the better part of a minute: start one, launch the editor while it runs, and the build lands on top of a process that was not there to be found. The loader in it segfaults on the next assembly it touches
# and the desktop session goes with it - measured, twice, in one afternoon. So the build says it is happening, and the launcher refuses to start while it is - see Launcher.Main. Removed however this script leaves, including a signal, so a build that dies does not lock the editor out.
touch game/.building
trap 'rm -f game/.building' EXIT INT TERM

for script in ./engine/Core/build-linux.sh ./engine/Core/build-macos.sh ./engine/Core/build-web.sh; do
	if [ -f "$script" ]; then
		chmod +x "$script"
	fi
done

case "${1:-all}" in
  native|linux)
    ./engine/Core/build-linux.sh
    ;;
  macos)
    ./engine/Core/build-macos.sh
    ;;
  web)
    ./engine/Core/build-web.sh
    ;;
  android)
    ./engine/Core/build-android.sh
    ;;
  android-ffmpeg)
    ./engine/Core/build-android-ffmpeg.sh
    ;;
  natives)
    # Every core this machine can build. The three that need something the machine may not have - a Mac, the NDK, Emscripten
    # - say so and are skipped rather than failing the lot: a person asking for all of them wants the ones they can have.
    ./engine/Core/build-linux.sh
    for other in macos web android; do
      echo
      echo "--- $other"
      ./engine/Core/build-"$other".sh || echo "skipped $other"
    done
    ;;
  managed)
    dotnet run --project ./engine/Tools/RintenBuild/RintenBuild.csproj -- build --skip-native
    ;;
  shaders)
    dotnet run --project ./engine/Tools/RintenBuild/RintenBuild.csproj -- build-shaders
    ;;
  all)
    ./engine/Core/build-linux.sh
    dotnet run --project ./engine/Tools/RintenBuild/RintenBuild.csproj -- build
    dotnet run --project ./engine/Tools/RintenBuild/RintenBuild.csproj -- build-shaders
    ;;
  *)
    echo "Unknown target: $1" >&2
    echo "Usage: ./build.sh [linux|macos|web|android|android-ffmpeg|natives|managed|shaders|all] [--steam]" >&2
    exit 1
    ;;
esac

if [ "$steam" = 1 ]; then
	dotnet run --project ./engine/Tools/RintenBuild/RintenBuild.csproj -- steam-upload
fi
