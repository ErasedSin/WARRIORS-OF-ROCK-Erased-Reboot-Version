# rewor - ReXGlue Recompiled Project
#
# SDK boilerplate lives in generated/rexglue.cmake, which 'rexglue codegen'
# writes. Run codegen once before the first configure:
#   rexglue codegen rewor_manifest.toml
#
# The project is named 'rewor' without the underscore because the SDK strips
# separators from the name when it writes generated/rexglue.cmake.

cmake_minimum_required(VERSION 3.25)
project(rewor LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/generated/rexglue.cmake")
    message(FATAL_ERROR
        "generated/rexglue.cmake not found. Run 'rexglue codegen rewor_manifest.toml' "
        "from the project root first.")
endif()
include(generated/rexglue.cmake)

include(FetchContent)
option(REWOR_ENABLE_DISCORD_RPC "Enable Discord Rich Presence via discord-rpc" ON)
if(REWOR_ENABLE_DISCORD_RPC)
    set(BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
    set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
    set(CLANG_FORMAT_CMD OFF CACHE STRING "" FORCE)
    FetchContent_Declare(discord_rpc
        GIT_REPOSITORY https://github.com/discord/discord-rpc.git
        GIT_TAG v3.4.0
    )
    FetchContent_MakeAvailable(discord_rpc)
endif()

set(REWOR_SOURCES
    src/main.cpp
    src/discord_rpc.cpp
    src/sampler.cpp
    src/thread_waits.cpp
    src/options_menu.cpp
    src/notcd.cpp
    src/dlc.cpp
    src/mic.cpp
    src/online.cpp
    src/timer.cpp
    src/intro.cpp
    src/quit_prompt.cpp
    src/framerate.cpp
    src/profile_picture.cpp
)

if(WIN32)
    list(APPEND REWOR_SOURCES app_icon.rc)
    add_executable(rewor WIN32 ${REWOR_SOURCES})
else()
    add_executable(rewor ${REWOR_SOURCES})
endif()

rexglue_setup_target(rewor GPU_PLUGINS xenos)

if(REWOR_ENABLE_DISCORD_RPC)
    target_link_libraries(rewor PRIVATE discord-rpc)
    target_compile_definitions(rewor PRIVATE REWOR_HAS_DISCORD_RPC=1)
endif()

option(REWOR_DEV "Enable developer logging (TRACE, StubTracker)" OFF)
if(REWOR_DEV)
    target_compile_definitions(rewor PRIVATE REWOR_DEV=1)
endif()

set_target_properties(rewor PROPERTIES OUTPUT_NAME "Guitar Hero Warriors of Rock")

# A linker map next to the exe: it names the function at each address, which
# turns the samples of rewor_sample_file (src/main.cpp) into guest functions.
if(WIN32 AND CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    target_link_options(rewor PRIVATE "LINKER:/MAP:${CMAKE_BINARY_DIR}/rewor.map")
endif()

# The CRT init hook in main.cpp relies on __try/__except catching hardware
# faults, which needs asynchronous exception handling.
if(WIN32)
    if(MSVC AND NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang")
        target_compile_options(rewor PRIVATE /EHa)
    elseif(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
        target_compile_options(rewor PRIVATE -fasync-exceptions)
    endif()
endif()
