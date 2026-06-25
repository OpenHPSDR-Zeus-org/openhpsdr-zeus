# SPDX-License-Identifier: GPL-2.0-or-later
#
# Openhpsdr-Zeus Audio Unit (AUv2) host bridge — macOS ONLY.
#
#   cmake -B build -DCMAKE_BUILD_TYPE=Release
#   cmake --build build
#
# Output: build/libzeus-au-bridge.dylib
#
# This target builds ONLY on Apple. On any other platform the project
# configures to a no-op so a cross-platform `cmake` over native/** never
# tries to compile AudioToolbox code with MSVC/GCC. The managed loader
# (AuBridgeNative) finds no dylib on Win/Linux and degrades to clean
# passthrough — mirroring the VST3 bridge's graceful-degrade behaviour.

cmake_minimum_required(VERSION 3.20)
project(zeus-au-bridge VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(APPLE)
    enable_language(OBJCXX)

    add_library(zeus-au-bridge SHARED
        src/bridge.mm
    )

    target_include_directories(zeus-au-bridge
        PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)

    # bridge.mm uses ARC-free manual lifetime (C AudioToolbox handles), so
    # do NOT enable -fobjc-arc here — the AudioComponentInstance lifecycle
    # is owned explicitly via AudioComponentInstanceDispose.
    find_library(AUDIOTOOLBOX_LIB    AudioToolbox)
    find_library(AUDIOUNIT_LIB       AudioUnit)
    find_library(AVFOUNDATION_LIB    AVFoundation)
    find_library(COREFOUNDATION_LIB  CoreFoundation)
    find_library(COREAUDIO_LIB       CoreAudio)
    # Foundation backs the Objective-C @autoreleasepool used to drain the
    # autoreleased CFString/AU temporaries during component enumeration.
    find_library(FOUNDATION_LIB      Foundation)
    target_link_libraries(zeus-au-bridge PRIVATE
        ${AUDIOTOOLBOX_LIB}
        ${AUDIOUNIT_LIB}
        ${AVFOUNDATION_LIB}
        ${COREFOUNDATION_LIB}
        ${COREAUDIO_LIB}
        ${FOUNDATION_LIB})

    # Strip non-ABI symbols; keep AudioToolbox internals off the export table.
    set_target_properties(zeus-au-bridge PROPERTIES
        CXX_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN ON)

    install(TARGETS zeus-au-bridge
        LIBRARY DESTINATION lib
        RUNTIME DESTINATION bin
        ARCHIVE DESTINATION lib)
    install(FILES include/zau.h DESTINATION include)
else()
    message(STATUS "zeus-au-bridge is macOS-only — nothing to build on this platform.")
endif()
