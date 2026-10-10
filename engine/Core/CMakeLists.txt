# rintencore - the native engine core.
#
# One shared library, built from our own C++ plus statically linked MIT
# dependencies, so the game ships a single file per platform and no dependency
# can be swapped out from under it at runtime.
#
# Build it with build-win64.cmd, which drops the result straight into
# game/bin/win64 where the managed side loads its natives from.

cmake_minimum_required(VERSION 3.24)

project(rintencore
	VERSION 0.1.0
	DESCRIPTION "Rinten engine core"
	LANGUAGES C CXX
)

set(CMAKE_C_STANDARD 17)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Static CRT. Box3D asks for it in its own CMakeLists and a mismatch across the
# two halves of this DLL is a real bug, not a warning to silence. It also means
# the shipped DLL needs no redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

# --- dependencies ------------------------------------------------------------
# Vendored, and linked statically: rintencore is the only native file we ship.

# Box3D's own CMakeLists builds samples, tests and docs only when it is the top
# level project, so adding it here gives us just the library.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
add_subdirectory(extern/box3d EXCLUDE_FROM_ALL)

# Box2D, for scenes whose physics runs in the plane. Vendored from
# erincatto/box2d main at 77619f4f (2026-09-16, version 3.2.0 - the last tag,
# v3.1.1, predates motion locks and joint frames, which the 2D backend is
# written against). Same treatment as Box3D: just the library, exported under
# its own b2 names. The two share no unprefixed symbol: Box3D's are b3, Box2D's
# b2, and each keeps its internals hidden.
add_subdirectory(extern/box2d EXCLUDE_FROM_ALL)

# --- the core ----------------------------------------------------------------

set(RINTENCORE_SOURCES
	src/fbx_shim.c
	extern/ufbx/ufbx.c
	src/image.c
	src/audio_shim.c
	extern/miniaudio/miniaudio.c
	extern/stb/stb_vorbis_impl.c
	extern/lz4/lz4.c
	extern/lz4/lz4hc.c
	extern/lz4/lz4frame.c
	extern/lz4/xxhash.c
)

# No render sources on any platform now. The Vulkan renderer is C# over
# Silk.NET (Core/Backend/Render/Vulkan) and the browser's is C# over the page's
# own WebGPU (Core/Backend/Render/WebGpu), so what this library builds is the
# same everywhere: third-party C and two shims over it.

# A static archive on the web so the managed WASM links it in; a shared library
# everywhere else, which is the one file the game loads at runtime.
if(EMSCRIPTEN)
	add_library(rintencore STATIC ${RINTENCORE_SOURCES})
else()
	add_library(rintencore SHARED ${RINTENCORE_SOURCES})
endif()

# Single-header libraries; one translation unit each defines the implementation.
# Slang is not here at all any more: the managed side opens the library that
# ships beside the engine and drives it through its own vtables (Slang.cs). The
# headers stay in extern/slang as the reference those vtable slots are counted
# from.
target_include_directories(rintencore PRIVATE extern/ufbx extern/stb extern/miniaudio extern/lz4)

target_compile_definitions(rintencore PRIVATE RINTENCORE_BUILD)

# miniaudio does playback and decoding here, and nothing else. Its resource
# manager and node graph are things this engine already has of its own, and
# building them would be a second answer to the same question. The decoders are
# wav, flac and mp3, which is what source audio in a project actually is; ogg
# comes from stb_vorbis, beside it in this list.
target_compile_definitions(rintencore PRIVATE
	MA_NO_ENCODING
	MA_NO_GENERATION
	MA_NO_RESOURCE_MANAGER
	MA_NO_NODE_GRAPH
	MA_NO_ENGINE
	MA_NO_CUSTOM
)

# Which window systems a build can make a surface for is not decided here. It
# was, because the loader's function pointers only existed for the VK_USE_PLATFORM_*
# a build defined, and a platform whose headers were missing was a build error.
# The managed loader asks the driver at run time instead: the instance is
# offered every surface extension the loader admits to, and the window says
# which kind it is. The browser is not here either - it draws through the
# page's own WebGPU, from C#, and this library never sees a surface.
if(EMSCRIPTEN)
	# The exception mechanism has to be the one the .NET runtime is linked
	# with, which is the WASM one: BrowserWasmApp.targets defaults
	# WasmEnableExceptionHandling to true and passes -fwasm-exceptions.
	# Objects built with -fexceptions instead - Emscripten's older
	# JavaScript-based unwinding - ask the final link for
	# __cxa_find_matching_catch_2, _3 and __resumeException, which a
	# -fwasm-exceptions link does not have. That is a link failure, not a
	# throw that misbehaves. Nothing here is C++ any more, so nothing emits
	# unwind tables, but the flag costs nothing and the next C++ file to
	# arrive would otherwise break the page rather than this file.
	#
	# SIMD because the runtime is linked with -msimd128, so the module
	# already requires a browser that has it, and building our half without
	# it only leaves Box3D's vector paths compiled out.
	set(RINTEN_WASM_FLAGS -fwasm-exceptions -msimd128)

	# And threads, on the same rule as the two above: whatever the .NET runtime
	# is linked with, this has to be compiled with.
	#
	# A runtime built with WasmEnableThreads links with --shared-memory, and
	# wasm-ld refuses that if any object in the link was built without the
	# atomics and bulk-memory features:
	#
	#   wasm-ld: error: --shared-memory is disallowed by core.c.o because it
	#            was not compiled with 'atomics' or 'bulk-memory' features
	#
	# So the archive is built twice and the publish picks the one that matches -
	# see build-web.sh and the NativeFileReference in Rinten-Web.csproj. The
	# third-party libraries already ship both (HarfBuzz's pack has an mt,simd
	# build beside its plain one); ours did not, and that alone is what kept a
	# browser build to one thread.
	if(RINTEN_WASM_THREADS)
		list(APPEND RINTEN_WASM_FLAGS -pthread)
		target_link_options(rintencore PUBLIC -pthread)
	endif()

	target_compile_options(rintencore PRIVATE ${RINTEN_WASM_FLAGS})
	target_link_options(rintencore PUBLIC -fwasm-exceptions)

	# Box3D and Box2D are separate targets, and their objects go into our archive.
	target_compile_options(box3d PRIVATE ${RINTEN_WASM_FLAGS})
	target_compile_options(box2d PRIVATE ${RINTEN_WASM_FLAGS})
endif()

# --- what the managed core calls by name ------------------------------------
# The engine's own logic is C# now, and this library is the third-party C it
# calls: Box3D, Box2D, lz4, stb_image, ufbx, miniaudio. Each is exported under its
# own names rather than wrapped, so the managed binding is the library's manual
# and a passthrough function is never a place to be wrong in.
if(WIN32)
	set(RINTEN_EXPORT "__declspec(dllexport)")
else()
	set(RINTEN_EXPORT "__attribute__((visibility(\"default\")))")
endif()

# Box3D: its headers export under box3d_EXPORTS, which CMake defines only for a
# shared build. Whole-archive, because the linker would otherwise keep only the
# objects something in this library referenced, and the managed side references
# all of them.
target_compile_definitions(box3d PRIVATE box3d_EXPORTS)
target_compile_definitions(box2d PRIVATE box2d_EXPORTS)

if(EMSCRIPTEN)
	# There is no link step here - this build makes an archive, and an archive
	# is not linked, it is collected. target_link_libraries on a static library
	# records a usage requirement for whoever links it later and copies no
	# objects in, so WHOLE_ARCHIVE below is not merely unnecessary here, it does
	# nothing at all: it is a linker option and nothing links.
	#
	# The result was an archive with every symbol in it except Box3D's, which is
	# exactly the half that matters - and it failed silently, because a missing
	# symbol in an archive is only an error when something finally looks for it.
	#
	# So the objects go in directly. One file called rintencore.a, holding
	# everything the managed side imports as "rintencore", which is what the
	# WASM module links and what Rinten-Web.csproj names.
	target_sources(rintencore PRIVATE $<TARGET_OBJECTS:box3d> $<TARGET_OBJECTS:box2d>)
	add_dependencies(rintencore box3d box2d)
else()
	target_link_libraries(rintencore PRIVATE $<LINK_LIBRARY:WHOLE_ARCHIVE,box3d> $<LINK_LIBRARY:WHOLE_ARCHIVE,box2d>)
endif()

# stb_image: its API macro defaults to extern.
target_compile_definitions(rintencore PRIVATE
	"STBIDEF=${RINTEN_EXPORT}"
)

# lz4 exports on GCC and Clang by itself; MSVC wants asking.
if(WIN32)
	target_compile_definitions(rintencore PRIVATE LZ4_DLL_EXPORT=1)
endif()

if(UNIX AND NOT APPLE AND NOT ANDROID)
	target_link_libraries(rintencore PRIVATE dl pthread m)
endif()

# Android's own libraries: the log the system reads, the native activity the
# window comes from, and the dynamic loader miniaudio opens its audio backend
# with. Nothing here is loaded on any other platform.
if(ANDROID)
	target_link_libraries(rintencore PRIVATE log android dl)
endif()

# --- Basis Universal: a web build's pictures ----------------------------------
# Vendored from BinomialLLC/basis_universal tag v2_50 (Apache 2.0, with zstd
# under BSD and its few third-party headers under MIT - see extern/basisu/NOTICE
# and LICENSES, which a build that ships this carries). The export turns every
# picture a material reads into a KTX2 of UASTC blocks; the page hands those to
# the browser's transcoder, which turns them into whatever the device reads -
# see StandaloneExporter.Pictures and BrowserTextures.
#
# The editor's half only: the encoder, on the desktops. No game opens it, so
# the web and Android builds of this library leave it out. Its plain C API is
# exported under its own names, bu_*, the way the rest of this library's are:
# visibility on the one file that defines them, and a .def where the linker
# asks for one.
if(NOT EMSCRIPTEN AND NOT ANDROID)
	set(BASISU_ENCODER_SOURCES
		extern/basisu/encoder/basisu_backend.cpp
		extern/basisu/encoder/basisu_basis_file.cpp
		extern/basisu/encoder/basisu_comp.cpp
		extern/basisu/encoder/basisu_enc.cpp
		extern/basisu/encoder/basisu_etc.cpp
		extern/basisu/encoder/basisu_frontend.cpp
		extern/basisu/encoder/basisu_gpu_texture.cpp
		extern/basisu/encoder/basisu_pvrtc1_4.cpp
		extern/basisu/encoder/basisu_resampler.cpp
		extern/basisu/encoder/basisu_resample_filters.cpp
		extern/basisu/encoder/basisu_ssim.cpp
		extern/basisu/encoder/basisu_uastc_enc.cpp
		extern/basisu/encoder/basisu_bc7e_scalar.cpp
		extern/basisu/encoder/basisu_dds_export.cpp
		extern/basisu/encoder/basisu_bc7enc.cpp
		extern/basisu/encoder/jpgd.cpp
		extern/basisu/encoder/basisu_kernels_sse.cpp
		extern/basisu/encoder/basisu_bc15_spmd.cpp
		extern/basisu/encoder/basisu_bc15_spmd_sse.cpp
		extern/basisu/encoder/basisu_opencl.cpp
		extern/basisu/encoder/pvpngreader.cpp
		extern/basisu/encoder/basisu_uastc_hdr_4x4_enc.cpp
		extern/basisu/encoder/basisu_astc_hdr_6x6_enc.cpp
		extern/basisu/encoder/basisu_astc_hdr_common.cpp
		extern/basisu/encoder/basisu_astc_ldr_common.cpp
		extern/basisu/encoder/basisu_astc_ldr_encode.cpp
		extern/basisu/encoder/basisu_astc_ldr_fencode.cpp
		extern/basisu/encoder/basisu_xbc7_encode.cpp
		extern/basisu/encoder/basisu_tinyexr.cpp
		extern/basisu/encoder/3rdparty/android_astc_decomp.cpp
		extern/basisu/transcoder/basisu_transcoder.cpp
		extern/basisu/zstd/zstd.c
	)

	add_library(basisu_encoder STATIC ${BASISU_ENCODER_SOURCES})
	set_target_properties(basisu_encoder PROPERTIES CXX_STANDARD 17)

	set(BASISU_DEFINES BASISU_SUPPORT_OPENCL=0 BASISU_SUPPORT_ASTCENC=0 BASISD_SUPPORT_KTX2_ZSTD=1)

	# The encoder's SSE 4.1 kernels on x86-64, where every machine the editor
	# runs on has them; plain code elsewhere.
	if(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64|amd64")
		list(APPEND BASISU_DEFINES BASISU_SUPPORT_SSE=1)
		if(NOT MSVC)
			target_compile_options(basisu_encoder PRIVATE -msse4.1)
		endif()
	else()
		list(APPEND BASISU_DEFINES BASISU_SUPPORT_SSE=0)
	endif()

	target_compile_definitions(basisu_encoder PRIVATE ${BASISU_DEFINES})

	if(MSVC)
		target_compile_options(basisu_encoder PRIVATE /W0)
	else()
		target_compile_options(basisu_encoder PRIVATE -w)
	endif()

	target_sources(rintencore PRIVATE extern/basisu/encoder/basisu_wasm_api.cpp
		extern/basisu/encoder/basisu_wasm_transcoder_api.cpp src/hdr_shim.cpp)
	set_source_files_properties(extern/basisu/encoder/basisu_wasm_api.cpp
		extern/basisu/encoder/basisu_wasm_transcoder_api.cpp src/hdr_shim.cpp PROPERTIES
		COMPILE_DEFINITIONS "${BASISU_DEFINES}"
		COMPILE_OPTIONS "$<IF:$<CXX_COMPILER_ID:MSVC>,/W0,-w;-fvisibility=default>"
		CXX_STANDARD 17)

	if(WIN32)
		target_sources(rintencore PRIVATE src/basisu.def)
	endif()

	target_link_libraries(rintencore PRIVATE basisu_encoder)

	if(UNIX AND NOT APPLE)
		target_link_libraries(rintencore PRIVATE pthread)
	endif()
endif()

# --- Basis Universal's transcoder: a phone's pictures ---------------------------
# A Vulkan device with no BC - nearly every phone - turns a build's KTX2 of UASTC
# blocks into ASTC or ETC2 as it loads them (plan В7), rather than taking every
# picture as four bytes a pixel. The desktops have the transcoder in the encoder
# above; Android takes it alone, with zstd for the KTX2's supercompression, and
# the same plain C API under its own names, bt_*. See CoreBasisTranscoder.
if(ANDROID)
	add_library(basisu_transcoder STATIC
		extern/basisu/transcoder/basisu_transcoder.cpp
		extern/basisu/zstd/zstddeclib.c)
	set_target_properties(basisu_transcoder PROPERTIES CXX_STANDARD 17)
	target_compile_definitions(basisu_transcoder PRIVATE BASISD_SUPPORT_KTX2_ZSTD=1)
	target_compile_options(basisu_transcoder PRIVATE -w)

	target_sources(rintencore PRIVATE extern/basisu/encoder/basisu_wasm_transcoder_api.cpp)
	set_source_files_properties(extern/basisu/encoder/basisu_wasm_transcoder_api.cpp PROPERTIES
		COMPILE_DEFINITIONS "BASISD_SUPPORT_KTX2_ZSTD=1"
		COMPILE_OPTIONS "-w;-fvisibility=default"
		CXX_STANDARD 17)

	target_link_libraries(rintencore PRIVATE basisu_transcoder)
endif()

if(MSVC)
	target_compile_options(rintencore PRIVATE /W4 /WX)
else()
	target_compile_options(rintencore PRIVATE -Wall -Wextra -Werror)
endif()

# Symbols in the release build. A crash inside the renderer arrives as a minidump
# with a stack of offsets from the nearest exported name, which says nothing about
# which function it was; a pdb turns the same dump into function names and lines.
# The code generated is the same - /Zi only writes the symbols out, and the two
# /OPT flags put back the folding that /DEBUG turns off.
if(MSVC)
	target_compile_options(rintencore PRIVATE /Zi)
	target_link_options(rintencore PRIVATE /DEBUG /OPT:REF /OPT:ICF)
endif()

# Vendored single-header libraries are held to their own standard, not ours:
# our warnings are about our code, and there is nothing to be done about
# somebody else's except carry a patch we would have to keep re-applying.
set(RINTENCORE_VENDORED
	src/image.c
	extern/miniaudio/miniaudio.c
	extern/stb/stb_vorbis_impl.c
	extern/lz4/lz4.c
	extern/lz4/lz4hc.c
	extern/lz4/lz4frame.c
	extern/lz4/xxhash.c
)

if(MSVC)
	set_source_files_properties(${RINTENCORE_VENDORED} PROPERTIES COMPILE_OPTIONS "/W0")
else()
	set_source_files_properties(${RINTENCORE_VENDORED} PROPERTIES COMPILE_OPTIONS "-w")
endif()

# Deterministic maths: never let the compiler fuse a multiply and an add, or
# two machines disagree about where a body came to rest.
if(NOT MSVC)
	target_compile_options(rintencore PRIVATE -ffp-contract=off)
endif()

set_target_properties(rintencore PROPERTIES
	PREFIX ""                     # rintencore.dll / rintencore.so, not librintencore
	RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
	LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
	ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# Look beside ourselves for anything this library opens at runtime. Slang was
# the reason for this - the shader compiler used to be here and dlopen searches
# only the system paths unless the caller carries a runpath, so the Linux build
# found no compiler with every file sitting right next to it. Slang is opened
# by the managed side now, by full path; the runpath stays for whatever a
# vendored library opens beside itself.
if(EMSCRIPTEN)
	# The archive is linked into the WASM module; there is nothing to dlopen.
elseif(UNIX AND NOT APPLE)
	set_target_properties(rintencore PROPERTIES
		BUILD_RPATH "$ORIGIN"
		INSTALL_RPATH "$ORIGIN"
	)
elseif(APPLE)
	set_target_properties(rintencore PROPERTIES
		BUILD_RPATH "@loader_path"
		INSTALL_RPATH "@loader_path"
	)
endif()
