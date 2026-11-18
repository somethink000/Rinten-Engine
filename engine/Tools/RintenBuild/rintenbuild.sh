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

# The files a publish is out of date against: the tool's own sources, and the props and targets every project under
# engine/ is built with. bin/ and obj/ hold the publish's own output - the marker below lives in bin/ too - so they are
# pruned rather than walked.
sources_newer_than()
{
	find "$tool" \( -path "$tool/bin" -o -path "$tool/obj" \) -prune -o \
		-type f \( -name '*.cs' -o -name '*.csproj' \) -newer "$1" -print -quit
}

shared_newer_than()
{
	local shared
	for shared in "$tool/../../Directory.Build.props" "$tool/../../Directory.Build.targets"; do
		if [ -f "$shared" ] && [ "$shared" -nt "$1" ]; then
			echo "$shared"
			return
		fi
	done
}

# A publish that failed - no native toolchain, a platform it cannot target - is remembered for the sources it failed
# on. Without that every build pays the same seconds again to learn the same thing; touching a source or deleting the
# marker asks for another try, which is what installing the toolchain and carrying on looks like.
failed="$tool/bin/aot/.no-aot"

publish=1
if [ -x "$binary" ] && [ -z "$(sources_newer_than "$binary")" ] && [ -z "$(shared_newer_than "$binary")" ]; then
	publish=0
fi

if [ "$publish" = 1 ]; then
	if [ -f "$failed" ] && [ -z "$(sources_newer_than "$failed")" ] && [ -z "$(shared_newer_than "$failed")" ]; then
		exec dotnet run --project "$tool/RintenBuild.csproj" -- "$@"
	fi

	# PublishAot on the command line rather than in the project, so only the publish pays for the native compiler.
	if ! dotnet publish "$tool/RintenBuild.csproj" -c Release -o "$tool/bin/aot" --nologo -v:q -p:PublishAot=true; then
		touch "$failed"
		echo "rintenbuild: could not publish the AOT tool here - running it with dotnet run instead" >&2
		exec dotnet run --project "$tool/RintenBuild.csproj" -- "$@"
	fi

	rm -f "$failed"
fi

exec "$binary" "$@"
