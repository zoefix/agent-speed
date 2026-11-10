cmake_minimum_required(VERSION 3.22)
project(AgentSpeedLinux VERSION 1.7.1 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

option(AGENTSPEED_BUILD_TESTS "Build the core tests" ON)
option(AGENTSPEED_BUILD_APP "Build the Qt application" OFF)
option(AGENTSPEED_BUILD_RELAY "Build the relay for remote hosts (REMOTE 3)" ON)
option(AGENTSPEED_STATIC "Link executables fully statically (the musl relay build)" OFF)

if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    add_compile_options(-Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wno-sign-conversion)
endif()

add_subdirectory(core)

# QUOTAS 5.5: the status line helper, installed beside the app's executable.
if(AGENTSPEED_BUILD_APP OR AGENTSPEED_BUILD_TESTS)
    add_subdirectory(statusline)
endif()

if(AGENTSPEED_BUILD_APP)
    add_subdirectory(app)
endif()

if(AGENTSPEED_BUILD_RELAY)
    add_subdirectory(relay)
endif()

if(AGENTSPEED_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
    if(AGENTSPEED_BUILD_RELAY)
        add_subdirectory(relay/tests)
    endif()
endif()
