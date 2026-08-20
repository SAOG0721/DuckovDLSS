cmake_minimum_required(VERSION 3.21)
project(duckov_dlss_bridge LANGUAGES CXX)

if(NOT DEFINED STREAMLINE_ROOT AND DEFINED ENV{STREAMLINE_ROOT})
    set(STREAMLINE_ROOT "$ENV{STREAMLINE_ROOT}")
endif()

if(NOT DEFINED UNITY_NATIVE_PLUGIN_ROOT AND DEFINED ENV{UNITY_NATIVE_PLUGIN_ROOT})
    set(UNITY_NATIVE_PLUGIN_ROOT "$ENV{UNITY_NATIVE_PLUGIN_ROOT}")
endif()

if(NOT EXISTS "${STREAMLINE_ROOT}/external/ngx-sdk/include/nvsdk_ngx.h")
    message(FATAL_ERROR "Set STREAMLINE_ROOT to NVIDIA Streamline SDK 2.12")
endif()
if(NOT EXISTS "${UNITY_NATIVE_PLUGIN_ROOT}/Unity/IUnityInterface.h")
    message(FATAL_ERROR "Set UNITY_NATIVE_PLUGIN_ROOT to Unity-NativeRenderingPlugin/PluginSource/source")
endif()

set(NGX_ROOT "${STREAMLINE_ROOT}/external/ngx-sdk")

find_program(FXC_EXECUTABLE
    NAMES fxc.exe fxc
    HINTS
        "C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64"
        "C:/Program Files (x86)/Windows Kits/10/bin/x64"
)
if(NOT FXC_EXECUTABLE)
    message(FATAL_ERROR "fxc.exe is required to precompile the Gaussian USM shader")
endif()

set(GENERATED_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated")
set(SHARPEN_SHADER_HEADER "${GENERATED_DIR}/gaussian_usm_cs.h")
add_custom_command(
    OUTPUT "${SHARPEN_SHADER_HEADER}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${GENERATED_DIR}"
    COMMAND "${FXC_EXECUTABLE}" /nologo /T cs_5_0 /E main /O3 /Ges
        /Qstrip_debug /Qstrip_reflect /Fh "${SHARPEN_SHADER_HEADER}"
        /Vn g_gaussianUsmCs "${CMAKE_CURRENT_SOURCE_DIR}/src/gaussian_usm_cs.hlsl"
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/src/gaussian_usm_cs.hlsl"
    VERBATIM
)

add_library(duckov-dlss-bridge SHARED
    src/duckov_dlss_bridge.cpp
    "${SHARPEN_SHADER_HEADER}"
)
target_compile_features(duckov-dlss-bridge PRIVATE cxx_std_17)
target_compile_definitions(duckov-dlss-bridge PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(duckov-dlss-bridge PRIVATE /utf-8)
endif()
target_include_directories(duckov-dlss-bridge PRIVATE
    "${NGX_ROOT}/include"
    "${UNITY_NATIVE_PLUGIN_ROOT}"
    "${GENERATED_DIR}"
)
target_link_libraries(duckov-dlss-bridge PRIVATE
    d3d11
    "${NGX_ROOT}/lib/Windows_x86_64/nvsdk_ngx_d.lib"
)
set_target_properties(duckov-dlss-bridge PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin"
)
