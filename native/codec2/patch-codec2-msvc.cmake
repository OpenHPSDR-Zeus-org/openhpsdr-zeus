# patch-codec2-msvc.cmake — make drowe67/codec2 1.2.0 build under an MSVC-style
# driver (MSVC proper or clang-cl). Run as a FetchContent PATCH_COMMAND:
#
#   ${CMAKE_COMMAND} -DCODEC2_SRC=<SOURCE_DIR> -P patch-codec2-msvc.cmake
#
# codec2 1.2.0 assumes a GCC/Clang-on-Unix toolchain and applies GCC-only
# global C flags + links Unix libm unconditionally, which an MSVC-style driver
# rejects (D8021 on -Wno-strict-overflow / -O3; LNK1181 on m.lib). Each of these
# is guarded behind `if(NOT MSVC)` — CMake sets MSVC=TRUE for BOTH cl and
# clang-cl, so:
#   * cl       — skips the bad flags (but cl can't compile codec2's C99 _Complex
#                OFDM/filter code anyway; Windows must use clang-cl — see the CI).
#   * clang-cl — skips the bad flags and builds the _Complex code fine.
#   * gcc/clang/MinGW (MSVC=FALSE) — unchanged: flags + libm apply as upstream.
#
# Idempotent: a sentinel comment gates re-application so re-running (or an
# incremental reconfigure) is a no-op.

cmake_minimum_required(VERSION 3.20)

set(_sentinel "# ZEUS-CODEC2-MSVC-PATCH")

# --- top-level CMakeLists.txt: global C flags --------------------------------
set(_top "${CODEC2_SRC}/CMakeLists.txt")
file(READ "${_top}" _c)
if(NOT _c MATCHES "ZEUS-CODEC2-MSVC-PATCH")
    string(REPLACE
[==[set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -Wall -Wno-strict-overflow")]==]
[==[# ZEUS-CODEC2-MSVC-PATCH
if(NOT MSVC)
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -Wall -Wno-strict-overflow")
endif()]==]
        _c "${_c}")
    string(REPLACE
[==[set(CMAKE_C_FLAGS_DEBUG "-g -O2 -DDUMP")
set(CMAKE_C_FLAGS_RELEASE "-O3")]==]
[==[if(NOT MSVC)
set(CMAKE_C_FLAGS_DEBUG "-g -O2 -DDUMP")
set(CMAKE_C_FLAGS_RELEASE "-O3")
endif()]==]
        _c "${_c}")
    file(WRITE "${_top}" "${_c}")
    message(STATUS "codec2: patched top-level CMakeLists.txt for MSVC-style drivers")
else()
    message(STATUS "codec2: top-level CMakeLists.txt already patched (skip)")
endif()

# --- src/CMakeLists.txt: generate_codebook libm link -------------------------
set(_src "${CODEC2_SRC}/src/CMakeLists.txt")
file(READ "${_src}" _s)
if(NOT _s MATCHES "ZEUS-CODEC2-MSVC-PATCH")
    string(REPLACE
[==[    add_executable(generate_codebook generate_codebook.c)
    target_link_libraries(generate_codebook m)]==]
[==[    # ZEUS-CODEC2-MSVC-PATCH
    add_executable(generate_codebook generate_codebook.c)
    if(NOT MSVC)
        target_link_libraries(generate_codebook m)
    endif()]==]
        _s "${_s}")
    file(WRITE "${_src}" "${_s}")
    message(STATUS "codec2: patched src/CMakeLists.txt generate_codebook libm link")
else()
    message(STATUS "codec2: src/CMakeLists.txt already patched (skip)")
endif()
