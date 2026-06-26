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

# Decoder source set (adds wsprd.c's CLI driver renamed to a callable entry +
# the Fano/Jelinek/OSD-stub/FFT files). The shared lib exposes zeus_wspr_decode
# on POSIX; Windows ships encode+synth only until the MSVC port (wsprd.c uses
# getopt/unistd and zeus_wspr_decode.c uses mkdtemp/pthreads).
set(WSPR_DECODE_SRC
    zeus_wspr_decode.c
    osdwspr_stub.c
    ${WSPR_VENDOR}/wsprd.c
    ${WSPR_VENDOR}/jelinek.c
    ${WSPR_VENDOR}/metric_tables.c
    ${WSPR_VENDOR}/pffft.c
)

# Rename wsprd.c's CLI main() to an internal callable entry (hidden visibility
# keeps it unexported; only zeus_wspr_* are public).
set_source_files_properties(${WSPR_VENDOR}/wsprd.c
    PROPERTIES COMPILE_DEFINITIONS "main=wsprd_cli_main")

if(WIN32)
    add_library(zeus_wspr SHARED zeus_wspr.c ${WSPR_ENCODE_SRC})
    target_include_directories(zeus_wspr PRIVATE ${WSPR_VENDOR})
    set_target_properties(zeus_wspr PROPERTIES OUTPUT_NAME "zeus_wspr" PREFIX "")
    target_compile_definitions(zeus_wspr PRIVATE _USE_MATH_DEFINES _CRT_SECURE_NO_WARNINGS)
else()
    find_package(Threads REQUIRED)
    add_library(zeus_wspr SHARED zeus_wspr.c ${WSPR_ENCODE_SRC} ${WSPR_DECODE_SRC})
    target_include_directories(zeus_wspr PRIVATE ${WSPR_VENDOR})
    target_compile_definitions(zeus_wspr PRIVATE ZEUS_WSPR_LINK_WSPRD)
    target_link_libraries(zeus_wspr PRIVATE m Threads::Threads)
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

    # Decode round-trip gate (POSIX only — captures the decoder's stdout via
    # dup2). Compiles the full vendored decoder with wsprd.c's main() renamed to
    # a callable entry, plus the no-op OSD stub (osdwspr.f90 is Fortran/optional).
    # Proves encode -> synth -> decode recovers the message.
    if(NOT WIN32)
        add_executable(zeus_wspr_roundtrip
            test/zeus_wspr_roundtrip.c
            zeus_wspr.c
            ${WSPR_ENCODE_SRC}
            ${WSPR_DECODE_SRC}
        )
        target_include_directories(zeus_wspr_roundtrip PRIVATE ${WSPR_VENDOR})
        # wsprd.c's main() is already renamed via set_source_files_properties above.
        # zeus_wspr.c must NOT also define printdata here (wsprd.c owns it).
        target_compile_definitions(zeus_wspr_roundtrip PRIVATE ZEUS_WSPR_LINK_WSPRD)
        target_link_libraries(zeus_wspr_roundtrip PRIVATE m Threads::Threads)
        add_test(NAME wspr_decode_roundtrip COMMAND zeus_wspr_roundtrip)
    endif()
endif()
