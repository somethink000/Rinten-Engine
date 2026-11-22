#!/bin/bash
# Builds the browser player into game/bin/wasm32: rintencore's dependencies as a wasm archive, and the .NET launcher that draws with them. The renderer is not in the archive. It is C# - Core/Backend/Render/Browser - and it reaches the browser's WebGPU through wwwroot/webgpu.js, so what this builds on the native side is the same third-party C every other platform gets. ./build-web.sh            incremental ./build-web.sh fresh
# delete the build dir first Needs Emscripten, which the .NET wasm-tools workload carries - the same one that relinks the runtime at publish time, which is the point: an archive built by a different Emscripten either fails to link or produces a page that does not start. dotnet workload install wasm-tools-net10 An emsdk already on PATH is used instead when its version matches the workload's. Keep LF line endings or Linux will not execute this.

set -e

SRC="$(cd "$(dirname "$0")" && pwd)"
BLD="$SRC/build/wasm32"
DEST="$SRC/../../game/bin/wasm32"

# --- Emscripten ------------------------------------------------------------- From the wasm-tools workload rather than from PATH. The workload ships three packs - the SDK (clang and emscripten), the node it runs its scripts on, and a prebuilt sysroot cache - and the .emscripten
# config inside the SDK pack is written to read the first two out of the environment (DOTNET_EMSCRIPTEN_LLVM_ROOT, _NODE_JS, _BINARYEN_ROOT), which is what the .NET build itself sets before it calls emcc. Set the same ones here and the two halves are built by one toolchain.

pack() {
	# Newest version of a workload pack, wherever the SDK was installed: a
	# user-local workload lands in ~/.dotnet, a machine-wide one beside dotnet.
	local name="$1" found=""

	for root in "$HOME/.dotnet/packs" "$(dirname "$(readlink -f "$(command -v dotnet)")")/packs"; do
		[ -d "$root" ] || continue

		# $name carries a * and is deliberately unquoted so the shell expands
		# it; the version directory under it is the second *.
		found="$(ls -d "$root"/$name/*/ 2>/dev/null | sort -V | tail -1)"
		[ -n "$found" ] && break
	done

	echo "$found"
}

EMSDK_PACK="$(pack "Microsoft.NET.Runtime.Emscripten.*.Sdk.linux-x64")"
EMNODE_PACK="$(pack "Microsoft.NET.Runtime.Emscripten.*.Node.linux-x64")"
EMCACHE_PACK="$(pack "Microsoft.NET.Runtime.Emscripten.*.Cache.linux-x64")"

if [ -n "$EMSDK_PACK" ] && [ -n "$EMNODE_PACK" ] && [ -n "$EMCACHE_PACK" ]; then
	export DOTNET_EMSCRIPTEN_LLVM_ROOT="${EMSDK_PACK}tools/bin"
	export DOTNET_EMSCRIPTEN_BINARYEN_ROOT="${EMSDK_PACK}tools"
	export DOTNET_EMSCRIPTEN_NODE_JS="${EMNODE_PACK}tools/bin/node"

	export EM_CONFIG="${EMSDK_PACK}tools/emscripten/.emscripten"
	export EM_CACHE="${EMCACHE_PACK}tools/emscripten/cache"

	export PATH="${EMSDK_PACK}tools/emscripten:${EMSDK_PACK}tools/bin:${EMNODE_PACK}tools/bin:$PATH"

	WANTED="$(cat "${EMSDK_PACK}tools/emscripten/emscripten-version.txt" 2>/dev/null | tr -d '"' | tr -d '[:space:]')"
	echo "emscripten $WANTED, from the wasm-tools workload"
elif command -v emcc >/dev/null; then
	# Somebody's own emsdk. Allowed, because a machine that builds the runtime from source
	# has one, but only when the .NET side would use the same version - see the note above.
	echo "emscripten from PATH: $(emcc --version | head -1)"
	echo "WARNING: the wasm-tools workload was not found, so the archive and the" >&2
	echo "         .NET relink may not be built by the same Emscripten." >&2
else
	echo "ERROR: no Emscripten." >&2
	echo "  Looked for the workload packs Microsoft.NET.Runtime.Emscripten.*.{Sdk,Node,Cache}.linux-x64" >&2
	echo "  under ~/.dotnet/packs and beside dotnet, and for emcc on PATH." >&2
	echo "  Install them with: dotnet workload install wasm-tools-net10" >&2
	exit 1
fi

if ! command -v emcc >/dev/null || ! command -v emcmake >/dev/null; then
	echo "ERROR: the Emscripten found has no emcc/emcmake in it." >&2
	exit 1
fi

# Ninja if there is one, make otherwise: this builds a few hundred translation
# units and the generator is not worth failing over.
if command -v ninja >/dev/null; then
	GENERATOR="Ninja"
else
	GENERATOR="Unix Makefiles"
fi

if [ "$1" = "fresh" ]; then
	rm -rf "$BLD"
fi

# The archive twice: once as every browser build has had it, and once with threads. A .NET runtime built with WasmEnableThreads links with --shared-memory, and wasm-ld refuses that unless every object in the link was compiled with the atomics and bulk-memory features. So the two cannot share an archive, and the publish picks by name - see the NativeFileReference in Rinten-Web.csproj.
# Building both here rather than making it a switch means a threaded publish never fails on a stale single-threaded archive, which is a link error a long way from its cause. Both keep the name rintencore.a and differ by the folder they sit in: the name is what every [DllImport] in the engine resolves against, so it is part of the contract and not a label. The threaded one lives in mt/.
build_core() {
	local into="$1" dir="$2" threads="$3"

	emcmake cmake -S "$SRC" -B "$dir" -G "$GENERATOR" -DCMAKE_BUILD_TYPE=Release \
		-DRINTEN_WASM_THREADS="$threads"
	cmake --build "$dir"

	mkdir -p "$into"
	if [ -f "$dir/bin/rintencore.a" ]; then
		cp -f "$dir/bin/rintencore.a" "$into/rintencore.a"
	elif [ -f "$dir/bin/librintencore.a" ]; then
		cp -f "$dir/bin/librintencore.a" "$into/rintencore.a"
	fi
}

build_core "$DEST" "$BLD" OFF
build_core "$DEST/mt" "$BLD-mt" ON

# The page's own scripts. The program itself - the engine and the game compiled ahead of time into one module by NativeAOT-LLVM - is not built here: the
# web export compiles it with the game's code inside (StandaloneExporter.WebModule, plan stage Н), so there is no prebuilt player and no _framework to copy.
WEBROOT="$SRC/../Launcher/RintenWeb/wwwroot"
if [ -d "$WEBROOT" ]; then
	rm -rf "$DEST/_framework" "$DEST/player.html"
	cp -f "$WEBROOT/index.html" "$DEST/index.html"
	# The handle tables the engine's WebGPU backend draws through - WebGPU's and the same
	# one over WebGL2. Without them the page loads, the runtime starts, and nothing renders.
	cp -f "$WEBROOT/webgpu.js" "$DEST/webgpu.js"
	cp -f "$WEBROOT/webgl2.js" "$DEST/webgl2.js"
	# And the headset. Without it index.html has no RintenXr to bind and a
	# VR build loads, draws, and has no way into a headset at all.
	cp -f "$WEBROOT/webxr.js" "$DEST/webxr.js"
	# And the picture transcoder a web build's pictures are turned by - see
	# basis.js. Without it every picture a material reads stays empty.
	for f in basis.js basis_worker.js basis_transcoder.js basis_transcoder.wasm basis_transcoder.NOTICE.txt; do
		cp -f "$WEBROOT/$f" "$DEST/$f"
	done
fi
cp -f "$SRC/src/web/native.js" "$DEST/native.js"

WEB_MODELS="$SRC/../Launcher/RintenWeb/wwwroot/models"
if [ -d "$WEB_MODELS" ]; then
	mkdir -p "$DEST/models"
	cp -a "$WEB_MODELS/." "$DEST/models/"
fi

WEB_ASSETS="${RINTEN_WEB_ASSETS:-$SRC/../../game/templates/game.minimal/Assets}"
WEB_SCENE="${RINTEN_WEB_SCENE:-scenes/minimal.scene}"
if [ -d "$WEB_ASSETS" ]; then
	cp -a "$WEB_ASSETS/." "$DEST/"
	if [ -f "$WEB_ASSETS/$WEB_SCENE" ]; then
		cp -f "$WEB_ASSETS/$WEB_SCENE" "$DEST/web-start.scene"
	fi
fi

SHADER_DIR="$SRC/../../game/addons/base/Assets/shaders"
mkdir -p "$DEST/shaders"
for packed in standard.shader_c shadow.shader_c sky.shader_c; do
	if [ -f "$SHADER_DIR/$packed" ]; then
		cp -f "$SHADER_DIR/$packed" "$DEST/shaders/$packed"
	fi
done

# The two lists a page reads before anything else: what content is in this build, and what the build is. The editor's web export writes the same
# two files - this is the same build with no project behind it, so it says so: a scene to look at, flown with a free camera, and no game code.
python3 - "$DEST" <<'PY'
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
asset_roots = {"models", "materials", "textures", "shaders", "sounds", "scenes", "prefabs", "particles"}
entries = []

for path in root.rglob("*"):
	if not path.is_file():
		continue
	relative = path.relative_to(root).as_posix()
	first = relative.split("/", 1)[0]
	if relative != "web-start.scene" and first not in asset_roots:
		continue
	entries.append(relative)

(root / "web-assets.txt").write_text("\n".join(sorted(entries)) + "\n")

(root / "web-game.json").write_text(json.dumps({
	"Name": "Rinten",
	"Ident": "local.dev",
	"StartScene": "web-start.scene" if (root / "web-start.scene").exists() else None,
	"Assemblies": [],
	"FreeCamera": True,
}, indent=2) + "\n")
PY

echo
echo "web player -> $DEST"
