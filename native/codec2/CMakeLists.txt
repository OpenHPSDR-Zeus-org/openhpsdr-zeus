# native/codec2/CMakeLists.txt — Zeus libcodec2 shared-library build
#
# Produces:
#   macOS   : libcodec2.dylib  (arm64, x86_64)
#   Linux   : libcodec2.so     (x86_64, arm64, Raspberry Pi)
#   Windows : codec2.dll       (x64, arm64) — no "lib" prefix
#
# This is the FreeDV modem / Codec 2 library from drowe67/codec2. Zeus
# P/Invokes it for FreeDV digital voice. The shared library is fetched
# straight from the upstream repo with CMake FetchContent and built through
# codec2's OWN CMake (required — codec2 generates version.h and its codebook
# sources at configure time, so the upstream build must run).
#
# Upstream pin
# ------------
# GIT_TAG is pinned to a SPECIFIC release tag so that updates are a deliberate
# ref bump, never a silent moving-HEAD change. Current pin:
#
#     drowe67/codec2  tag 1.2.0  (commit 06d4c11e699b0351765f10398abb4f663a984f36)
#
# To update: bump CODEC2_GIT_TAG below to the new upstream release tag, rebuild
# on all platforms, and re-run the version verification. See VENDORING.md for
# the full update procedure.
#
# Intentionally EXCLUDED: LPCNet / 2020 / 2020B
# ---------------------------------------------
# We do NOT set LPCNET / LPCNET_BUILD_DIR. This keeps libcodec2 dependency-free
# (no FFTW, no neural-net runtime, no model files) and cross-platform-clean. The
# resulting library still carries the classic FreeDV modes — 700C, 700D, 700E,
# 1600, and 800XA — which is the full set Zeus targets. The ML-based 2020 / 2020B
# modes are out of scope and would drag in LPCNet; do not enable them here.
#
# No vendored source
# ------------------
# codec2 source is NOT copied into this repo. FetchContent pulls the pinned tag
# at configure time. "Update from the freedv repo" == bump CODEC2_GIT_TAG.

cmake_minimum_required(VERSION 3.20)
project(zeus_codec2
    VERSION 1.2.0
    DESCRIPTION "Codec 2 / FreeDV modem library, packaged for Zeus"
    LANGUAGES C
)

include(FetchContent)

# Pinned upstream release tag. Bump deliberately; see header + VENDORING.md.
set(CODEC2_GIT_TAG "1.2.0" CACHE STRING "drowe67/codec2 git tag to fetch and build")

set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# -----------------------------------------------------------------------------
# codec2 build options, forced before the subproject is populated.
#
#   BUILD_SHARED_LIBS=ON  — codec2's `add_library(codec2 ...)` honours this, so
#                           we get libcodec2.{dylib,so}/codec2.dll, the form
#                           .NET NativeLibrary resolves and Zeus P/Invokes.
#   UNITTEST=OFF          — skip codec2's unit-test binaries.
#   LPCNET=OFF            — classic modes only, no neural-net dependency.
#
# These are normal cache entries; setting them here (FORCE) before
# FetchContent_MakeAvailable means the codec2 subproject picks them up.
# -----------------------------------------------------------------------------
set(BUILD_SHARED_LIBS ON  CACHE BOOL "Build codec2 as a shared library" FORCE)
set(UNITTEST          OFF CACHE BOOL "Do not build codec2 unit tests"   FORCE)
set(LPCNET            OFF CACHE BOOL "Do not build codec2 with LPCNet"   FORCE)

FetchContent_Declare(
    codec2
    GIT_REPOSITORY https://github.com/drowe67/codec2.git
    GIT_TAG        ${CODEC2_GIT_TAG}
    GIT_SHALLOW    TRUE
    # codec2 1.2.0 assumes GCC/Clang-on-Unix: it sets GCC-only global C flags and
    # links Unix libm unconditionally, which an MSVC-style driver (cl / clang-cl)
    # rejects. Guard those behind `if(NOT MSVC)` so Windows can build with
    # clang-cl (codec2's OFDM/filter code is C99 _Complex, which cl cannot
    # compile — Windows must use clang-cl; see build-native-libs.yml). Idempotent.
    PATCH_COMMAND ${CMAKE_COMMAND} -DCODEC2_SRC=<SOURCE_DIR>
                  -P ${CMAKE_CURRENT_SOURCE_DIR}/patch-codec2-msvc.cmake
)

# Populate + add codec2's CMake tree. This defines the `codec2` target (and a
# set of CLI executables we do not ship — build only the `codec2` target, e.g.
#   cmake --build <dir> --target codec2
# The classic modes need NO external deps, so configure succeeds without FFTW.)
FetchContent_MakeAvailable(codec2)

# -----------------------------------------------------------------------------
# Output naming: match .NET NativeLibrary resolution defaults, exactly like
# native/wdsp/CMakeLists.txt does.
#   macOS   : libcodec2.dylib
#   Linux   : libcodec2.so
#   Windows : codec2.dll   (no "lib" prefix — PREFIX "" on WIN32)
#
# codec2's own src/CMakeLists.txt already names the target `codec2` and does
# not set OUTPUT_NAME, so the default prefix/suffix give the right file on
# UNIX. Windows would otherwise produce libcodec2.dll; force PREFIX "" to drop
# the lib prefix. We guard with TARGET in case a future upstream rename moves
# the library target.
# -----------------------------------------------------------------------------
if(TARGET codec2)
    set_target_properties(codec2 PROPERTIES OUTPUT_NAME codec2)
    if(WIN32)
        set_target_properties(codec2 PROPERTIES PREFIX "")
    endif()
else()
    message(WARNING
        "codec2: expected a target named 'codec2' from upstream but none was "
        "found. If upstream renamed the library target, update the "
        "set_target_properties() block in native/codec2/CMakeLists.txt.")
endif()

message(STATUS "codec2: build type = ${CMAKE_BUILD_TYPE}")
message(STATUS "codec2: pinned upstream tag = ${CODEC2_GIT_TAG}")
message(STATUS "codec2: LPCNet / 2020 modes = OFF (classic modes only)")
