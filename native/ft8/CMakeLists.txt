# native/ft8/CMakeLists.txt — Zeus FT8/FT4 decode+encode shim build
#
# Produces:
#   macOS   : libzeus_ft8.dylib  (arm64, x86_64)
#   Linux   : libzeus_ft8.so     (x86_64, arm64)
#   Windows : zeus_ft8.dll       (x64, arm64)
#
# Source is vendored kgoba/ft8_lib (Kārlis Goba, MIT — see vendor/LICENSE) plus
# zeus_ft8.c, which wraps it in the small stable C ABI the Zeus C# P/Invoke
# (Zeus.Dsp.Ft8) binds against. The FT8/FT4 protocol constants ft8_lib
# reproduces are public-domain per the QEX FT4/FT8 paper. See README.md and
# ATTRIBUTIONS.md.
#
# No external dependencies: ft8_lib bundles its own KISS FFT (BSD), so there is
# nothing to find_package — this is the simplest native target in the tree.

cmake_minimum_required(VERSION 3.20)
project(zeus_ft8
    VERSION 0.1.0
    DESCRIPTION "FT8/FT4 decode + encode — Zeus native digital-mode core"
    LANGUAGES C
)

set(CMAKE_C_STANDARD 11)          # _Thread_local in the shim
set(CMAKE_C_STANDARD_REQUIRED ON)
# Hide everything by default; the Zeus ABI marks only zeus_ft8_* exports
# visible, keeping ft8_lib's internal symbols out of the shared object.
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

set(FT8_VENDOR ${CMAKE_CURRENT_SOURCE_DIR}/vendor)

set(FT8_VENDOR_SRC
    ${FT8_VENDOR}/ft8/constants.c
    ${FT8_VENDOR}/ft8/crc.c
    ${FT8_VENDOR}/ft8/decode.c
    ${FT8_VENDOR}/ft8/encode.c
    ${FT8_VENDOR}/ft8/ldpc.c
    ${FT8_VENDOR}/ft8/message.c
    ${FT8_VENDOR}/ft8/text.c
    ${FT8_VENDOR}/common/monitor.c
    ${FT8_VENDOR}/fft/kiss_fft.c
    ${FT8_VENDOR}/fft/kiss_fftr.c
)

add_library(zeus_ft8 SHARED
    zeus_ft8.c
    ${FT8_VENDOR_SRC}
)

target_include_directories(zeus_ft8 PRIVATE ${FT8_VENDOR})
# MSVC gates M_PI behind _USE_MATH_DEFINES; define it for all sources on Windows.
target_compile_definitions(zeus_ft8 PRIVATE $<$<PLATFORM_ID:Windows>:_USE_MATH_DEFINES>)
# ft8_lib's message.c calls stpcpy() directly (POSIX, absent from the MSVC CRT).
# Force-include a compat shim on Windows so the vendored source stays pristine.
if(WIN32)
    target_compile_options(zeus_ft8 PRIVATE /FI${CMAKE_CURRENT_SOURCE_DIR}/win_compat.h)
endif()

# NOTE (Windows): ft8_lib's vendored decode.c / common/monitor.c use C99
# variable-length arrays, which MSVC cl.exe rejects. Configure the Windows build
# with the ClangCL toolset (clang supports VLAs), exactly as Zeus does for
# libspecbleach: `cmake -G "Visual Studio 17 2022" -T ClangCL ...`. See
# .github/workflows/build-native-libs.yml.

if(WIN32)
    set_target_properties(zeus_ft8 PROPERTIES OUTPUT_NAME "zeus_ft8" PREFIX "")
else()
    target_link_libraries(zeus_ft8 PRIVATE m)
    set_target_properties(zeus_ft8 PROPERTIES
        OUTPUT_NAME "zeus_ft8"
        PREFIX "${CMAKE_SHARED_LIBRARY_PREFIX}"
    )
endif()

# ---- decode-correctness self-test (CI gate) -------------------------------
# Builds a host executable that decodes the bundled reference WAV corpus and
# compares against the WSJT-X answer keys. Enabled by default; turn off with
# -DZEUS_FT8_TESTS=OFF for a lib-only build.
option(ZEUS_FT8_TESTS "Build the zeus_ft8 decode-correctness self-test" ON)
if(ZEUS_FT8_TESTS)
    enable_testing()
    add_executable(zeus_ft8_selftest
        test/zeus_ft8_selftest.c
        zeus_ft8.c
        ${FT8_VENDOR_SRC}
        ${FT8_VENDOR}/common/wave.c
    )
    target_include_directories(zeus_ft8_selftest PRIVATE ${FT8_VENDOR})
    target_compile_definitions(zeus_ft8_selftest PRIVATE $<$<PLATFORM_ID:Windows>:_USE_MATH_DEFINES>)
    if(WIN32)
        target_compile_options(zeus_ft8_selftest PRIVATE /FI${CMAKE_CURRENT_SOURCE_DIR}/win_compat.h)
    endif()
    if(NOT WIN32)
        target_link_libraries(zeus_ft8_selftest PRIVATE m)
    endif()
    add_test(NAME ft8_corpus_decode
        COMMAND zeus_ft8_selftest ${CMAKE_CURRENT_SOURCE_DIR}/test-vectors/wav)
endif()
