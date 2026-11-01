cmake_minimum_required(VERSION 3.20)
project(RigidIPCBackend LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Unity's native plugin API headers. Pinned to the editor this project targets because the
# physics backend protocol changes between Unity minor versions.
set(UNITY_PLUGIN_API_DIR "K:/Unity/6000.6.7f1/Editor/Data/PluginAPI" CACHE PATH "Unity PluginAPI include dir")

add_library(RigidIPCBackend SHARED src/RigidIPCBackend.cpp)
target_include_directories(RigidIPCBackend PRIVATE "${UNITY_PLUGIN_API_DIR}" "${UNITY_PLUGIN_API_DIR}/PhysicsBackend")
target_compile_definitions(RigidIPCBackend PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN _CRT_SECURE_NO_WARNINGS)
if (MSVC)
    target_compile_options(RigidIPCBackend PRIVATE /W3 /permissive- /utf-8)
    # Static CRT so the DLL has no redistributable dependency inside the editor/player.
    set_property(TARGET RigidIPCBackend PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

# Copy the built DLL into the Unity project so the editor picks it up. Turn off for a trial build
# while the editor is open (it holds the loaded DLL locked).
option(UNITY_COPY_PLUGIN "Copy the built DLL into Assets/Plugins/x86_64" ON)
if (UNITY_COPY_PLUGIN)
    set(UNITY_PLUGIN_OUT "${CMAKE_CURRENT_SOURCE_DIR}/../../Assets/Plugins/x86_64")
    add_custom_command(TARGET RigidIPCBackend POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${UNITY_PLUGIN_OUT}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:RigidIPCBackend>" "${UNITY_PLUGIN_OUT}/"
    )
endif()
