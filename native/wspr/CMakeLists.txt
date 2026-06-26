# native/wspr/CMakeLists.txt — Zeus WSPR core build
#
# Produces libzeus_wspr.{dylib,so} / zeus_wspr.dll. Wraps the vendored K1JT/K9AN
# wsprd (GPL-3, see vendor/ and ../../ATTRIBUTIONS.md) in the stable zeus_wspr_*
# C ABI for P/Invoke. pffft is bundled, so there is no FFTW dependency.
#
# CURRENT SCOPE: WSPR ENCODE only. The decoder (wsprd.c) is a CLI main() that
# must be refactored into a callable slot-decode entry before it joins the lib;
# its files (wsprd.c, jelinek.c, metric_tables.c, pffft.c) are vendored but not
# yet compiled here. See README.md.

cmake_minimum_required(VERSION 3.20)
project(zeus_wspr
    VERSION 0.1.0
    DESCRIPTION "WSPR encode (+ decode, forthcoming) — Zeus native digital-mode core"
    LANGUAGES C
)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

set(WSPR_VENDOR ${CMAKE_CURRENT_SOURCE_DIR}/vendor)

# Encoder file set: get_wspr_channel_symbols (wsprsim_utils) + encode (fano) +
# unpk_ (wsprd_utils) + nhash + tab. The decoder file set is added later.
set(WSPR_ENCODE_SRC
    ${WSPR_VENDOR}/wsprsim_utils.c
    ${WSPR_VENDOR}/fano.c
    ${WSPR_VENDOR}/wsprd_utils.c
    ${WSPR_VENDOR}/nhash.c
    ${WSPR_VENDOR}/tab.c
)

add_library(zeus_wspr SHARED
    zeus_wspr.c
    ${WSPR_ENCODE_SRC}
)

target_include_directories(zeus_wspr PRIVATE ${WSPR_VENDOR})

if(WIN32)
    set_target_properties(zeus_wspr PROPERTIES OUTPUT_NAME "zeus_wspr" PREFIX "")
    # MSVC: M_PI guard + POSIX shims (unistd.h etc.) via a force-included header,
    # and ClangCL for any C99 VLAs in the vendored decoder — wired in the CI
    # build step alongside the FT8 lib when decode lands.
    target_compile_definitions(zeus_wspr PRIVATE _USE_MATH_DEFINES _CRT_SECURE_NO_WARNINGS)
else()
    target_link_libraries(zeus_wspr PRIVATE m)
    set_target_properties(zeus_wspr PROPERTIES OUTPUT_NAME "zeus_wspr" PREFIX "${CMAKE_SHARED_LIBRARY_PREFIX}")
endif()

# ---- encode-correctness self-test (CI gate) -------------------------------
option(ZEUS_WSPR_TESTS "Build the zeus_wspr encode self-test" ON)
if(ZEUS_WSPR_TESTS)
    enable_testing()
    add_executable(zeus_wspr_selftest
        test/zeus_wspr_selftest.c
        zeus_wspr.c
        ${WSPR_ENCODE_SRC}
    )
    target_include_directories(zeus_wspr_selftest PRIVATE ${WSPR_VENDOR})
    if(WIN32)
        target_compile_definitions(zeus_wspr_selftest PRIVATE _USE_MATH_DEFINES _CRT_SECURE_NO_WARNINGS)
    else()
        target_link_libraries(zeus_wspr_selftest PRIVATE m)
    endif()
    add_test(NAME wspr_encode_sync COMMAND zeus_wspr_selftest)
endif()
