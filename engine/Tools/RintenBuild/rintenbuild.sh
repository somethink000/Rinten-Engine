#!/bin/bash
# Runs the build tool from a NativeAOT binary, publishing it first when its sources are newer than the one on disk.
# A publish is a few seconds once and nothing after that, and the binary starts in milliseconds instead of the
# half-second a `dotnet run` needs even when everything is already built. Falls back to `dotnet run` where an AOT
# publish cannot be made - no clang, a platform the toolchain has no target for - so a build still gets its tool.
set -euo pipefail

tool="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
binary="$tool/bin/aot/rintenbuild"

case "$(uname -s)" in
	MINGW*|MSYS*|CYGWIN*) binary="$binary.exe" ;;
esac

# The tool's own sources, and the props and targets every project under engine/ is built with.
newer=""
if [ -x "$binary" ]; then
	newer="$(find "$tool" \( -path "$tool/bin" -o -path "$tool/obj" \) -prune -o \
		-type f \( -name '*.cs' -o -name '*.csproj' \) -newer "$binary" -print -quit)"

	for shared in "$tool/../../Directory.Build.props" "$tool/../../Directory.Build.targets"; do
		if [ -f "$shared" ] && [ "$shared" -nt "$binary" ]; then
			newer="$shared"
			break
		fi
	done
fi

if [ ! -x "$binary" ] || [ -n "$newer" ]; then
	if ! dotnet publish "$tool/RintenBuild.csproj" -c Release -o "$tool/bin/aot" --nologo -v:q; then
		echo "rintenbuild: could not publish the AOT tool here - running it with dotnet run instead" >&2
		exec dotnet run --project "$tool/RintenBuild.csproj" -- "$@"
	fi
fi

exec "$binary" "$@"
