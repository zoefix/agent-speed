cmake_minimum_required(VERSION 3.22)
project(AgentSpeedLinux VERSION 1.5.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

option(AGENTSPEED_BUILD_TESTS "Build the core tests" ON)
option(AGENTSPEED_BUILD_APP "Build the Qt application" OFF)

if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    add_compile_options(-Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wno-sign-conversion)
endif()

add_subdirectory(core)

if(AGENTSPEED_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif()
