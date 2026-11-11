# mbedTLS 3 for the relay's HTTPS client (REMOTE 11.4) as the target agentspeed_mbedtls: the system's when it has
# version 3 (Alpine's static archives in the relay build), else a pinned release built here (Ubuntu ships 2.28).
if(TARGET agentspeed_mbedtls)
    return()
endif()
add_library(agentspeed_mbedtls INTERFACE)

find_package(MbedTLS 3 CONFIG QUIET)
if(MbedTLS_FOUND)
    if(AGENTSPEED_STATIC AND TARGET MbedTLS::mbedtls_static)
        target_link_libraries(agentspeed_mbedtls INTERFACE MbedTLS::mbedtls_static)
    else()
        target_link_libraries(agentspeed_mbedtls INTERFACE MbedTLS::mbedtls)
    endif()
    message(STATUS "mbedTLS ${MbedTLS_VERSION} from the system")
    return()
endif()

include(FetchContent)
# Extracted files get the extraction time (CMake 3.24+; CI's Ubuntu 22.04 has 3.22).
if(POLICY CMP0135)
    cmake_policy(SET CMP0135 NEW)
endif()
FetchContent_Declare(mbedtls
    URL https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-3.6.6/mbedtls-3.6.6.tar.bz2
    URL_HASH SHA256=8fb65fae8dcae5840f793c0a334860a411f884cc537ea290ce1c52bb64ca007a)
set(ENABLE_PROGRAMS OFF CACHE BOOL "" FORCE)
set(ENABLE_TESTING OFF CACHE BOOL "" FORCE)
set(USE_SHARED_MBEDTLS_LIBRARY OFF CACHE BOOL "" FORCE)
set(USE_STATIC_MBEDTLS_LIBRARY ON CACHE BOOL "" FORCE)
set(GEN_FILES OFF CACHE BOOL "" FORCE)
set(MBEDTLS_FATAL_WARNINGS OFF CACHE BOOL "" FORCE)
set(DISABLE_PACKAGE_CONFIG_AND_INSTALL ON CACHE BOOL "" FORCE)
# The project's warning flags are for its own C++; the library builds with its own.
get_directory_property(agentspeed_saved_options COMPILE_OPTIONS)
set_directory_properties(PROPERTIES COMPILE_OPTIONS "")
FetchContent_MakeAvailable(mbedtls)
set_directory_properties(PROPERTIES COMPILE_OPTIONS "${agentspeed_saved_options}")
target_link_libraries(agentspeed_mbedtls INTERFACE mbedtls)
message(STATUS "mbedTLS 3.6.6 built from its release")
