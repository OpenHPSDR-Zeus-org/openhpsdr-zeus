# SPDX-License-Identifier: GPL-2.0-or-later
#
# Openhpsdr-Zeus VST3 bridge.
#
#   cmake -B build -DCMAKE_BUILD_TYPE=Release
#   cmake --build build
#
# Output: build/libzeus-vst-bridge.{so,dylib,dll}

cmake_minimum_required(VERSION 3.20)
project(zeus-vst-bridge VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Default to a stub build if vst3sdk is missing (works in CI / dev
# without the submodule). With the submodule in place, build the
# real hosting bridge.
set(VST3SDK_DIR "${CMAKE_CURRENT_SOURCE_DIR}/third_party/vst3sdk"
    CACHE PATH "Path to vendored vst3sdk")

if(EXISTS "${VST3SDK_DIR}/pluginterfaces/vst/ivstcomponent.h")
    message(STATUS "Building zeus-vst-bridge with vst3sdk at ${VST3SDK_DIR}")
    set(ZEUS_USE_VST3SDK ON)
else()
    message(STATUS "vst3sdk not vendored — building stub bridge (no VST3 loading)")
    set(ZEUS_USE_VST3SDK OFF)
endif()

if(ZEUS_USE_VST3SDK)
    # Minimal vst3sdk source subset: hosting helpers + base + pluginterfaces.
    # We deliberately skip vstgui4, samples, validator — they're not needed
    # to host VSTs and they pull a lot of transitive deps.
    set(VST3SDK_SOURCES
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/hostclasses.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/processdata.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/parameterchanges.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/pluginterfacesupport.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/eventlist.cpp
        ${VST3SDK_DIR}/public.sdk/source/common/commonstringconvert.cpp
        ${VST3SDK_DIR}/public.sdk/source/common/commoniids.cpp
        ${VST3SDK_DIR}/public.sdk/source/common/memorystream.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/vstinitiids.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/utility/stringconvert.cpp
        ${VST3SDK_DIR}/pluginterfaces/base/coreiids.cpp
        ${VST3SDK_DIR}/pluginterfaces/base/conststringtable.cpp
        ${VST3SDK_DIR}/pluginterfaces/base/funknown.cpp
        ${VST3SDK_DIR}/pluginterfaces/base/ustring.cpp
        ${VST3SDK_DIR}/base/source/baseiids.cpp
        ${VST3SDK_DIR}/base/source/fobject.cpp
        ${VST3SDK_DIR}/base/source/fbuffer.cpp
        ${VST3SDK_DIR}/base/source/fstring.cpp
        ${VST3SDK_DIR}/base/source/updatehandler.cpp
        ${VST3SDK_DIR}/base/thread/source/flock.cpp
        ${VST3SDK_DIR}/base/source/fdebug.cpp
    )

    if(APPLE)
        list(APPEND VST3SDK_SOURCES
            ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module_mac.mm)
    elseif(WIN32)
        list(APPEND VST3SDK_SOURCES
            ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module_win32.cpp)
    else()
        list(APPEND VST3SDK_SOURCES
            ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module_linux.cpp)
    endif()

    add_library(zeus-vst-bridge SHARED
        src/bridge.cpp
        ${VST3SDK_SOURCES}
    )

    target_include_directories(zeus-vst-bridge
        PRIVATE ${VST3SDK_DIR}
        PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/include
    )

    target_compile_definitions(zeus-vst-bridge PRIVATE
        RELEASE=$<IF:$<CONFIG:Release>,1,0>
        DEVELOPMENT=$<IF:$<CONFIG:Debug>,1,0>
    )

    if(APPLE)
        find_library(COREFOUNDATION_LIB CoreFoundation)
        find_library(FOUNDATION_LIB    Foundation)
        target_link_libraries(zeus-vst-bridge PRIVATE
            ${COREFOUNDATION_LIB} ${FOUNDATION_LIB})
        # vst3sdk's module_mac.mm requires ARC.
        set_source_files_properties(
            ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module_mac.mm
            PROPERTIES COMPILE_FLAGS "-fobjc-arc")
    elseif(UNIX AND NOT APPLE)
        # X11 hosts the plug-in editor window + run loop on Linux (bridge.cpp,
        # __linux__ path). find_package surfaces include dirs/libs portably.
        find_package(X11 REQUIRED)
        target_include_directories(zeus-vst-bridge PRIVATE ${X11_INCLUDE_DIR})
        target_link_libraries(zeus-vst-bridge PRIVATE dl pthread ${X11_LIBRARIES})
    endif()

    if(WIN32)
        # Editor hosting (bridge.cpp) creates a native window + COM
        # apartment for the plug-in GUI. These are in the MSVC default
        # lib set, but link them explicitly so the dependency is visible.
        target_link_libraries(zeus-vst-bridge PRIVATE user32 ole32)
    endif()
else()
    # Stub fallback. Build src/bridge_stub.cpp if present, else the real
    # bridge.cpp under a guard macro that no-ops the SDK includes.
    add_library(zeus-vst-bridge SHARED
        src/bridge.cpp
    )
    target_compile_definitions(zeus-vst-bridge PRIVATE ZEUS_VST_BRIDGE_STUB=1)
    target_include_directories(zeus-vst-bridge
        PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
    )
endif()

# Strip non-ABI symbols on platforms that support it.
if(NOT WIN32)
    set_target_properties(zeus-vst-bridge PROPERTIES
        CXX_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN ON)
endif()

install(TARGETS zeus-vst-bridge
    LIBRARY DESTINATION lib
    RUNTIME DESTINATION bin
    ARCHIVE DESTINATION lib)
install(FILES include/zvst.h DESTINATION include)
