#!/bin/bash
# Builds the engine on Linux, the way Bootstrap.bat does on Windows. Needs cmake, ninja, a C++20 compiler, the X11 and Wayland headers, and the .NET 10 SDK: sudo apt install cmake ninja-build clang libx11-dev libwayland-dev dotnet-sdk-10.0 Slang, for the shader step, is fetched by the build - engine/natives.json pins the release and `fetch-natives` unpacks it into game/bin/linuxsteamrt64. Skia and
# HarfBuzz come out of the NuGet restore. The one thing still to put there by hand is SDL3, for anything that opens a window: there are no Linux binary releases and no Ubuntu package, so libSDL3.so is built from the SDL source release. That build needs the X11, Wayland, xkbcommon, DBus and audio development packages listed in SDL's README-linux. Keep LF line endings or Linux will not execute this.

set -e

cd "$(dirname "$0")"

# Builds the native core then the managed engine; --skip-native keeps the rintencore an earlier build put in game/bin
# Add --clean to remove stale obj/.generated output before rebuilding
# Add --no-aot to skip compiling the editor's assemblies ahead of time - a publish of the whole closure, worth it to
# measure or play, worth skipping while editing C# in a loop
./engine/Tools/RintenBuild/rintenbuild.sh build "$@"

./engine/Tools/RintenBuild/rintenbuild.sh build-shaders
