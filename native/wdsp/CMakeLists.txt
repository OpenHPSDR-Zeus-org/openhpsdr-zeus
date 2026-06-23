# CMakeLists.txt — Zeus WDSP shared library build
#
# Produces:
#   macOS   : libwdsp.dylib  (arm64, x86_64)
#   Linux   : libwdsp.so     (x86_64, arm64)
#   Windows : wdsp.dll       (x64, arm64) — untested here, target is CMake-friendly
#
# Source is vendored upstream WDSP 1.29 (Warren Pratt) plus a linux_port.c
# portability shim. Only the PORT macro in comm.h is patched (→ WDSP_EXPORT in
# wdsp_export.h) so every upstream __declspec(dllexport) resolves to the right
# platform-specific visibility attribute.
#
# FFTW3 (double precision) is required for WDSP itself.
# FFTW3f (single precision) is additionally required when WDSP_WITH_NR4=ON
# because libspecbleach is single-precision.
#
# NR3 (RNNoise) and NR4 (libspecbleach / SBNR) are gated by separate flags:
#   * WDSP_WITH_NR3 — OFF by default. Turn ON after vendoring xiph/rnnoise under
#                    native/rnnoise/ (see native/rnnoise/VENDORING.md). Built
#                    without the default model — NR3 needs an operator-installed
#                    model at runtime.
#   * WDSP_WITH_NR4 — ON  by default. libspecbleach is vendored under
#                    native/libspecbleach/ (see Phase 1a of #162).

cmake_minimum_required(VERSION 3.20)
project(wdsp
    VERSION 1.29.0
    DESCRIPTION "WDSP DSP engine, packaged for Zeus"
    LANGUAGES C
)

option(WDSP_WITH_NR3 "Build with RNNoise (NR3) support — requires librnnoise (not yet vendored)" OFF)
option(WDSP_WITH_NR4 "Build with libspecbleach (NR4 / SBNR) support — vendored under native/libspecbleach/" ON)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# -----------------------------------------------------------------------------
# Source list (mirrors upstream WDSP 1.29 Makefile SOURCES, minus rnnr.c and
# sbnr.c which are gated below per-flag).
# -----------------------------------------------------------------------------
set(WDSP_SOURCES
    amd.c ammod.c amsq.c analyzer.c anf.c anr.c apfshadow.c bandpass.c
    calcc.c calculus.c cblock.c cfcomp.c cfir.c channel.c cmath.c compress.c
    delay.c dexp.c div.c doublepole.c eer.c emnr.c emph.c eq.c fcurve.c
    FDnoiseIQ.c fir.c firmin.c fmd.c fmmod.c fmsq.c gain.c gaussian.c gen.c
    icfir.c iir.c impulse_cache.c iobuffs.c iqc.c linux_port.c lmath.c main.c
    matchedCW.c meter.c meterlog10.c nbp.c nob.c nobII.c osctrl.c patchpanel.c
    resample.c rmatch.c RXA.c sender.c shift.c siphon.c slew.c snb.c ssql.c
    syncbuffs.c TXA.c utilities.c varsamp.c version.c wcpAGC.c wisdom.c zetaHat.c
)

# NR3 — RNNoise gate.
if(WDSP_WITH_NR3)
    list(APPEND WDSP_SOURCES rnnr.c)
else()
    list(APPEND WDSP_SOURCES stubs/nr3/rnnr_stub.c)
endif()

# NR4 — libspecbleach (SBNR) gate.
if(WDSP_WITH_NR4)
    list(APPEND WDSP_SOURCES sbnr.c)
else()
    list(APPEND WDSP_SOURCES stubs/nr4/sbnr_stub.c)
endif()

add_library(wdsp SHARED ${WDSP_SOURCES})

target_include_directories(wdsp
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
)

# Stubs are kept in per-NR subdirs (stubs/nr3/, stubs/nr4/) so we only add the
# stub include path that matches the OFF flag — otherwise the libspecbleach
# include path that comes via target_link_libraries(wdsp PRIVATE specbleach)
# below would collide with stubs/specbleach_adenoiser.h.
if(NOT WDSP_WITH_NR3)
    target_include_directories(wdsp PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/stubs/nr3)
endif()
if(NOT WDSP_WITH_NR4)
    target_include_directories(wdsp PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/stubs/nr4)
endif()

# -----------------------------------------------------------------------------
# Compiler flags
# -----------------------------------------------------------------------------
if(MSVC)
    target_compile_options(wdsp PRIVATE /O2 /fp:precise /W3 /wd4244 /wd4267)
    target_compile_definitions(wdsp PRIVATE _CRT_SECURE_NO_WARNINGS)
else()
    target_compile_options(wdsp PRIVATE
        -O3
        -pthread
        -Wno-parentheses
        -Wno-unused-result
        -Wno-unused-but-set-variable
        -Wno-unused-variable
        -Wno-unused-function
        -Wno-deprecated-non-prototype
        -Wno-implicit-function-declaration
        -Wno-incompatible-pointer-types
    )
    target_compile_definitions(wdsp PRIVATE _GNU_SOURCE)
    target_link_options(wdsp PRIVATE -pthread)
endif()

# -----------------------------------------------------------------------------
# FFTW3 (double precision) — required for WDSP core
# -----------------------------------------------------------------------------
find_package(PkgConfig QUIET)
set(_fftw_found FALSE)
set(FFTW_ROOT "" CACHE PATH "Optional FFTW install root containing fftw3.h and FFTW import libraries")
set(_fftw_hint_roots)
foreach(_fftw_hint "${FFTW_ROOT}" "$ENV{FFTW_ROOT}")
    if(_fftw_hint)
        list(APPEND _fftw_hint_roots "${_fftw_hint}")
    endif()
endforeach()

if(PkgConfig_FOUND)
    pkg_check_modules(FFTW3 QUIET fftw3)
    if(FFTW3_FOUND)
        target_include_directories(wdsp PRIVATE ${FFTW3_INCLUDE_DIRS})
        target_link_libraries(wdsp PRIVATE ${FFTW3_LIBRARIES})
        target_link_directories(wdsp PRIVATE ${FFTW3_LIBRARY_DIRS})
        set(_fftw_found TRUE)
    endif()
endif()

# vcpkg / CMake CONFIG mode — used when the vcpkg toolchain is active (Windows)
# or any platform where FFTW3 ships a CMake config package. This call also makes
# FFTW3::fftw3f available below for the libspecbleach build.
if(NOT _fftw_found)
    find_package(FFTW3 CONFIG QUIET)
    if(FFTW3_FOUND)
        target_link_libraries(wdsp PRIVATE FFTW3::fftw3)
        set(_fftw_found TRUE)
        message(STATUS "FFTW3 found via CMake config (vcpkg or system package)")
    endif()
endif()

# Local Windows / manually unpacked FFTW fallback. Thetis-style FFTW folders keep
# fftw3.h and the import libraries side-by-side rather than in include/lib.
if(NOT _fftw_found AND _fftw_hint_roots)
    foreach(_prefix IN LISTS _fftw_hint_roots)
        if(EXISTS "${_prefix}/include/fftw3.h")
            set(_fftw_include_dir "${_prefix}/include")
            set(_fftw_library_dirs "${_prefix}/lib" "${_prefix}")
        elseif(EXISTS "${_prefix}/fftw3.h")
            set(_fftw_include_dir "${_prefix}")
            set(_fftw_library_dirs "${_prefix}")
        else()
            continue()
        endif()

        find_library(FFTW3_LIBRARY NAMES fftw3 libfftw3-3 libfftw3 PATHS ${_fftw_library_dirs} NO_DEFAULT_PATH)
        if(FFTW3_LIBRARY)
            target_include_directories(wdsp PRIVATE "${_fftw_include_dir}")
            target_link_libraries(wdsp PRIVATE "${FFTW3_LIBRARY}")
            set(_fftw_found TRUE)
            message(STATUS "FFTW3 found via FFTW_ROOT: ${FFTW3_LIBRARY}")
            break()
        endif()
    endforeach()
endif()

# macOS Homebrew fallback (arm64: /opt/homebrew, x86_64: /usr/local).
if(NOT _fftw_found AND APPLE)
    foreach(_prefix /opt/homebrew/opt/fftw /usr/local/opt/fftw)
        if(EXISTS ${_prefix}/include/fftw3.h)
            target_include_directories(wdsp PRIVATE ${_prefix}/include)
            target_link_directories(wdsp PRIVATE ${_prefix}/lib)
            target_link_libraries(wdsp PRIVATE fftw3)
            set(_fftw_found TRUE)
            message(STATUS "FFTW3 found via Homebrew prefix: ${_prefix}")
            break()
        endif()
    endforeach()
endif()

if(NOT _fftw_found)
    message(FATAL_ERROR "FFTW3 not found. Install with: brew install fftw (macOS) | apt install libfftw3-dev (Linux) | see native/README.md for Windows.")
endif()

# -----------------------------------------------------------------------------
# libspecbleach (NR4 / SBNR) — vendored at native/libspecbleach/, built inline
# as a STATIC sub-target so it embeds into libwdsp without a separate runtime
# library to ship.
#
# Thetis's libspecbleach/CMakeLists.txt is Windows-only (hardcoded fftw_x64
# path, find_library lookup for the import .lib). We don't use it here — Zeus
# drives the build directly so we get cross-platform behaviour with one
# detection chain. Thetis's CMakeLists stays in-tree as upstream reference.
# -----------------------------------------------------------------------------
if(WDSP_WITH_NR4)
    set(LIBSPECBLEACH_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../libspecbleach)
    if(NOT EXISTS ${LIBSPECBLEACH_DIR}/include/specbleach_adenoiser.h)
        message(FATAL_ERROR "WDSP_WITH_NR4=ON but native/libspecbleach/ is missing or incomplete. See native/libspecbleach/VENDORING.md.")
    endif()

    file(GLOB_RECURSE SPECBLEACH_SRCS
        "${LIBSPECBLEACH_DIR}/src/*.c"
    )
    add_library(specbleach STATIC ${SPECBLEACH_SRCS})
    set_target_properties(specbleach PROPERTIES
        POSITION_INDEPENDENT_CODE ON
        C_VISIBILITY_PRESET hidden
        C_STANDARD 99
    )
    target_include_directories(specbleach
        PUBLIC ${LIBSPECBLEACH_DIR}/include
        PRIVATE ${LIBSPECBLEACH_DIR}/src
    )
    if(MSVC OR CMAKE_C_COMPILER_ID STREQUAL "Clang")
        target_compile_definitions(specbleach PRIVATE _USE_MATH_DEFINES)
    endif()
    if(NOT MSVC)
        # Quiet upstream-style warnings from libspecbleach so they don't churn
        # the build log; we don't own this source so we don't fix them.
        target_compile_options(specbleach PRIVATE
            -Wno-unused-variable -Wno-unused-function -Wno-unused-but-set-variable
            -Wno-incompatible-pointer-types -Wno-implicit-function-declaration
        )
    endif()

    # FFTW3f detection — prefers the FFTW3::fftw3f target the FFTW3 CONFIG
    # package above already exposes (vcpkg path), falls back to pkg-config
    # for system-package installs (Linux), and finally Homebrew on macOS.
    set(_fftw3f_found FALSE)
    if(TARGET FFTW3::fftw3f)
        target_link_libraries(specbleach PRIVATE FFTW3::fftw3f)
        set(_fftw3f_found TRUE)
        message(STATUS "FFTW3f found via FFTW3 CONFIG package (vcpkg)")
    endif()

    if(NOT _fftw3f_found AND PkgConfig_FOUND)
        pkg_check_modules(FFTW3F QUIET fftw3f)
        if(FFTW3F_FOUND)
            target_include_directories(specbleach PRIVATE ${FFTW3F_INCLUDE_DIRS})
            target_link_libraries(specbleach PRIVATE ${FFTW3F_LIBRARIES})
            target_link_directories(specbleach PRIVATE ${FFTW3F_LIBRARY_DIRS})
            set(_fftw3f_found TRUE)
            message(STATUS "FFTW3f found via pkg-config")
        endif()
    endif()

    # vcpkg fallback: vcpkg's FFTW3Config.cmake exposes only FFTW3::fftw3
    # (double precision) even though its fftw3 port builds fftw3f.lib
    # alongside. Find the single-precision library file directly — the
    # vcpkg toolchain has already augmented CMake's library search paths
    # to cover the installed tree. fftw3.h is the same header for all
    # three precisions, so we re-link FFTW3::fftw3 just to inherit its
    # INTERFACE_INCLUDE_DIRECTORIES.
    if(NOT _fftw3f_found)
        find_library(FFTW3F_LIBRARY NAMES fftw3f libfftw3f-3 libfftw3f)
        if(FFTW3F_LIBRARY)
            target_link_libraries(specbleach PRIVATE ${FFTW3F_LIBRARY})
            if(TARGET FFTW3::fftw3)
                target_link_libraries(specbleach PRIVATE FFTW3::fftw3)
            endif()
            set(_fftw3f_found TRUE)
            message(STATUS "FFTW3f found via find_library: ${FFTW3F_LIBRARY}")
        endif()
    endif()

    if(NOT _fftw3f_found AND _fftw_hint_roots)
        foreach(_prefix IN LISTS _fftw_hint_roots)
            if(EXISTS "${_prefix}/include/fftw3.h")
                set(_fftw_include_dir "${_prefix}/include")
                set(_fftw_library_dirs "${_prefix}/lib" "${_prefix}")
            elseif(EXISTS "${_prefix}/fftw3.h")
                set(_fftw_include_dir "${_prefix}")
                set(_fftw_library_dirs "${_prefix}")
            else()
                continue()
            endif()

            find_library(FFTW3F_LIBRARY_HINT NAMES fftw3f libfftw3f-3 libfftw3f PATHS ${_fftw_library_dirs} NO_DEFAULT_PATH)
            if(FFTW3F_LIBRARY_HINT)
                target_include_directories(specbleach PRIVATE "${_fftw_include_dir}")
                target_link_libraries(specbleach PRIVATE "${FFTW3F_LIBRARY_HINT}")
                set(_fftw3f_found TRUE)
                message(STATUS "FFTW3f found via FFTW_ROOT: ${FFTW3F_LIBRARY_HINT}")
                break()
            endif()
        endforeach()
    endif()

    if(NOT _fftw3f_found AND APPLE)
        foreach(_prefix /opt/homebrew/opt/fftw /usr/local/opt/fftw)
            if(EXISTS ${_prefix}/include/fftw3.h)
                target_include_directories(specbleach PRIVATE ${_prefix}/include)
                target_link_directories(specbleach PRIVATE ${_prefix}/lib)
                target_link_libraries(specbleach PRIVATE fftw3f)
                set(_fftw3f_found TRUE)
                message(STATUS "FFTW3f found via Homebrew prefix: ${_prefix}")
                break()
            endif()
        endforeach()
    endif()

    if(NOT _fftw3f_found)
        message(FATAL_ERROR "FFTW3f (single-precision) not found, required by libspecbleach for NR4. Install with: brew install fftw (macOS, includes fftw3f) | apt install libfftw3-dev (Linux, ditto) | vcpkg install fftw3 (Windows). To build without NR4, configure with -DWDSP_WITH_NR4=OFF.")
    endif()

    target_link_libraries(wdsp PRIVATE specbleach)
endif()

# -----------------------------------------------------------------------------
# librnnoise (NR3 / RNNoise) — vendored at native/rnnoise/, built inline as a
# STATIC sub-target so it embeds into libwdsp (same pattern as libspecbleach).
#
# Built WITHOUT the baked-in default model: Zeus requires the operator to
# install a model (RNNRloadModel / rnnoise_model_from_filename), so the large
# default-weights translation unit is excluded and the "stock model" path is
# gated off (xiph's --disable-default-model equivalent). NR3 is then inert until
# a model file is loaded. See native/rnnoise/VENDORING.md for the vendor step
# and the pinned upstream tag.
# -----------------------------------------------------------------------------
if(WDSP_WITH_NR3)
    set(RNNOISE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../rnnoise)
    if(NOT EXISTS ${RNNOISE_DIR}/include/rnnoise.h)
        message(FATAL_ERROR "WDSP_WITH_NR3=ON but native/rnnoise/ is missing or incomplete. See native/rnnoise/VENDORING.md.")
    endif()

    # Drop the compiled-in default-model data unit(s) so no stock weights ship.
    file(GLOB_RECURSE RNNOISE_SRCS "${RNNOISE_DIR}/src/*.c")
    list(FILTER RNNOISE_SRCS EXCLUDE REGEX ".*rnnoise_data.*\\.c$")
    add_library(rnnoise STATIC ${RNNOISE_SRCS})
    set_target_properties(rnnoise PROPERTIES
        POSITION_INDEPENDENT_CODE ON
        C_VISIBILITY_PRESET hidden
        C_STANDARD 99
    )
    target_include_directories(rnnoise PUBLIC ${RNNOISE_DIR}/include PRIVATE ${RNNOISE_DIR}/src)
    # USE_WEIGHTS_FILE gates off the stock-model code path (matches xiph's
    # --disable-default-model), so rnnoise_create(NULL) yields an inert denoiser
    # until RNNRloadModel installs a model.
    target_compile_definitions(rnnoise PRIVATE USE_WEIGHTS_FILE)
    if(NOT MSVC)
        target_compile_options(rnnoise PRIVATE
            -Wno-unused-variable -Wno-unused-function -Wno-unused-but-set-variable)
    endif()

    # rnnr.c does #include "rnnoise.h" — point it at the vendored header (the
    # stub include dir is intentionally NOT added when WDSP_WITH_NR3 is ON).
    target_include_directories(wdsp PRIVATE ${RNNOISE_DIR}/include)
    target_link_libraries(wdsp PRIVATE rnnoise)
endif()

# -----------------------------------------------------------------------------
# Windows system libraries: main.c calls AvSetMmThreadCharacteristics /
# AvSetMmThreadPriority / AvRevertMmThreadCharacteristics (Multimedia Class
# Scheduler "Pro Audio") which live in avrt.lib. Thetis's wdsp.vcxproj does
# the same.
# -----------------------------------------------------------------------------
if(WIN32)
    target_link_libraries(wdsp PRIVATE avrt)
endif()

# -----------------------------------------------------------------------------
# Output naming: match .NET NativeLibrary resolution defaults.
#   macOS   : libwdsp.dylib  (OUTPUT_NAME=wdsp + PREFIX=lib + .dylib)
#   Linux   : libwdsp.so
#   Windows : wdsp.dll       (no lib prefix)
# -----------------------------------------------------------------------------
set_target_properties(wdsp PROPERTIES
    OUTPUT_NAME wdsp
    C_VISIBILITY_PRESET hidden
)
if(WIN32)
    set_target_properties(wdsp PROPERTIES PREFIX "")
endif()

message(STATUS "wdsp: build type = ${CMAKE_BUILD_TYPE}")
message(STATUS "wdsp: NR3 (RNNoise) enabled = ${WDSP_WITH_NR3}")
message(STATUS "wdsp: NR4 (libspecbleach / SBNR) enabled = ${WDSP_WITH_NR4}")
