# native/radae/CMakeLists.txt
#
# Builds ONE shared library, `zeus_rade`, that decodes RADE V1 (Radio Autoencoder)
# off-air IQ to 16 kHz PCM. It is the cross-platform native base for Zeus's RADE
# support and is P/Invoked from the .NET side as a single library.
#
# Composition (all statically linked into the one .so/.dll/.dylib):
#   1. opus_dnn   — Xiph Opus + the DNN/FARGAN vocoder (built here via CMake with
#                   OPUS_DEEP_PLC=ON; provides fargan_synthesize / nnet).
#   2. radae_c    — David Rowe's pure-C, Python-free RADE modem (IQ -> 36-float
#                   feature frames). Weights are compiled in (rade_enc_data.c /
#                   rade_dec_data.c, ~24 MB each). No model files at runtime.
#   3. zeus_rade  — Zeus's thin shim (shim/zeus_rade.c): rade_rx() -> accumulate
#                   NB_TOTAL_FEATURES -> fargan_synthesize(), behind one streaming
#                   "complex IQ in -> int16 PCM out" surface.
#
# The three upstream slices are NOT committed to the Zeus git tree (~95 MB of
# weight sources). CI (build-native-libs.yml) vendors them into ${ZEUS_RADE_VENDOR}
# from the SHAs recorded in vendor/PROVENANCE.md before configuring this project.
#
# PROVEN: this exact composition decoded a 5-minute off-air RADE recording in WSL
# (Ubuntu-24.04) — 2620 synced ticks, 14 dB SNR, max amplitude 0.999969
# (full-scale real speech). See vendor/PROVENANCE.md.

cmake_minimum_required(VERSION 3.16)
project(zeus_rade C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# ---------------------------------------------------------------------------
# Options
# ---------------------------------------------------------------------------
# Root of the vendored upstream slices. CI populates this; for a local build,
# point it at a checkout of sv1eia/Thetis-RADE's "Project Files/lib" (see
# vendor/PROVENANCE.md for the pinned SHA and the exact sub-paths).
set(ZEUS_RADE_VENDOR "${CMAKE_CURRENT_SOURCE_DIR}/vendor"
    CACHE PATH "Directory containing radae_c/, opus_dnn/, freedv_text/ slices")

# Generic / Raspberry Pi fallback: disable all SIMD intrinsics. Also required on
# any host where the vendored opus_dnn tree is the MSVC-only slice (its arm/ and
# x86/ SIMD subtrees are stripped; see PROVENANCE.md). Default ON for portability.
option(OPUS_DISABLE_INTRINSICS "Disable Opus SIMD intrinsics (generic/RPi build)" ON)

set(RADAE_C   "${ZEUS_RADE_VENDOR}/radae_c")
set(OPUS_DNN  "${ZEUS_RADE_VENDOR}/opus_dnn")
set(FREEDV_TXT "${ZEUS_RADE_VENDOR}/freedv_text")

if(NOT EXISTS "${OPUS_DNN}/CMakeLists.txt")
  message(FATAL_ERROR
    "opus_dnn not found at ${OPUS_DNN}. Vendor the slices listed in "
    "native/radae/vendor/PROVENANCE.md (CI does this automatically).")
endif()

# ---------------------------------------------------------------------------
# 1. opus_dnn — static Opus + DNN/FARGAN. OPUS_DEEP_PLC pulls in the FARGAN
#    vocoder + nnet sources (dnn/fargan.c, dnn/fargan_data.c, dnn/nnet.c, ...).
#    Build as a static lib; we whole-archive it into zeus_rade so the FARGAN
#    symbols survive into the final shared object.
# ---------------------------------------------------------------------------
set(OPUS_BUILD_SHARED_LIBRARY OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_PROGRAMS       OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_TESTING        OFF CACHE BOOL "" FORCE)
set(OPUS_DEEP_PLC             ON  CACHE BOOL "" FORCE)  # FARGAN vocoder + nnet
# OPUS_DRED / OPUS_OSCE are NOT required for the V1 decode path (DEEP_PLC alone
# yielded full-scale decode in the proven WSL run); enable only if a future
# inference path needs them.
add_subdirectory("${OPUS_DNN}" "${CMAKE_CURRENT_BINARY_DIR}/opus_dnn" EXCLUDE_FROM_ALL)

set(OPUS_INCLUDE_DIRS
    "${OPUS_DNN}/include"
    "${OPUS_DNN}/dnn"
    "${OPUS_DNN}/celt"
    "${OPUS_DNN}/silk")

# ---------------------------------------------------------------------------
# 2. radae_c — Python-free RADE modem (IQ -> features) + compiled-in weights.
#    radc_api.c exposes the canonical rade_api.h surface the shim links against.
# ---------------------------------------------------------------------------
set(RADAE_C_SOURCES
    "${RADAE_C}/src/radc_acq.c"
    "${RADAE_C}/src/radc_api.c"
    "${RADAE_C}/src/radc_bpf.c"
    "${RADAE_C}/src/radc_dec.c"
    "${RADAE_C}/src/radc_demod.c"
    "${RADAE_C}/src/radc_enc.c"
    "${RADAE_C}/src/radc_mod.c"
    "${RADAE_C}/src/radc_modem.c"
    "${RADAE_C}/src/radc_rx.c"
    "${RADAE_C}/src/radc_tx.c"
    "${RADAE_C}/src/rade_dec_data.c"   # ~24 MB compiled-in decoder weights
    "${RADAE_C}/src/rade_enc_data.c")  # ~24 MB compiled-in encoder weights

# The two compiled-in weight tables are ~24 MB each of generated data — at -O2/-O3
# they cost minutes and gigabytes of RAM to compile for zero runtime benefit (they
# are data, not hot code). Pin them to -O1. (GCC / Clang / MinGW; MSVC ignores.)
if(NOT MSVC)
  set_source_files_properties(
    "${RADAE_C}/src/rade_enc_data.c"
    "${RADAE_C}/src/rade_dec_data.c"
    PROPERTIES COMPILE_OPTIONS "-O1")
endif()

# ---------------------------------------------------------------------------
# 3. zeus_rade shim
# ---------------------------------------------------------------------------
set(SHIM_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/shim/zeus_rade.c")

# ---------------------------------------------------------------------------
# The one shared library
# ---------------------------------------------------------------------------
add_library(zeus_rade SHARED ${RADAE_C_SOURCES} ${SHIM_SOURCES})

target_include_directories(zeus_rade PRIVATE
    "${RADAE_C}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/shim"
    ${OPUS_INCLUDE_DIRS})

# RADE_STATIC neutralises the DLL import/export + __stdcall decoration in
# rade_api.h (Thetis-RADE static-build patch); RADE_PYTHON_FREE selects the C
# encoder/decoder; IS_BUILDING_RADE_API matches the upstream vcxproj defines.
target_compile_definitions(zeus_rade PRIVATE
    RADE_STATIC
    RADE_PYTHON_FREE=1
    IS_BUILDING_RADE_API=1)

if(OPUS_DISABLE_INTRINSICS)
  target_compile_definitions(zeus_rade PRIVATE OPUS_DISABLE_INTRINSICS)
endif()

# Whole-archive the static opus so fargan_* / nnet symbols are retained even
# though only the shim references them transitively.
if(APPLE)
  target_link_libraries(zeus_rade PRIVATE
    -Wl,-force_load,$<TARGET_FILE:opus> m)
elseif(MSVC)
  target_link_libraries(zeus_rade PRIVATE opus)
  set_target_properties(zeus_rade PROPERTIES LINK_FLAGS "/WHOLEARCHIVE:opus")
else() # GNU/Clang ld
  target_link_libraries(zeus_rade PRIVATE
    -Wl,--whole-archive opus -Wl,--no-whole-archive m)
endif()

set_target_properties(zeus_rade PROPERTIES
    C_VISIBILITY_PRESET default
    OUTPUT_NAME zeus_rade
    VERSION 1.0
    SOVERSION 1)

# Windows: emit `zeus_rade.dll` (drop MinGW's "lib" prefix) so the file name
# matches what the .NET loader probes (FreeDvNativeLoader.RadeNativeFileName).
# Linux/macOS keep the conventional lib<name>.so / .dylib.
if(WIN32)
  set_target_properties(zeus_rade PROPERTIES PREFIX "")
endif()

# MinGW: statically link the GCC + winpthread runtimes so the DLL depends only on
# system libraries (KERNEL32 + UCRT) — never libgcc_s_seh-1.dll / libwinpthread-1.dll.
# PROVEN: yields a standalone zeus_rade.dll that decoded off-air RADE on Windows.
if(MINGW)
  target_link_options(zeus_rade PRIVATE -static -static-libgcc -Wl,-Bstatic)
endif()

# ---------------------------------------------------------------------------
# Optional: standalone decode-validation harness (proves the lib end-to-end).
# ---------------------------------------------------------------------------
option(ZEUS_RADE_BUILD_TEST "Build the zeus_rade_test decode harness" OFF)
if(ZEUS_RADE_BUILD_TEST)
  add_executable(zeus_rade_test "${CMAKE_CURRENT_SOURCE_DIR}/shim/zeus_rade_test.c")
  target_include_directories(zeus_rade_test PRIVATE
      "${RADAE_C}/src"
      "${CMAKE_CURRENT_SOURCE_DIR}/shim"
      ${OPUS_INCLUDE_DIRS})
  target_link_libraries(zeus_rade_test PRIVATE zeus_rade)
endif()
