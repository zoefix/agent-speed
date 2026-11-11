find_package(Threads REQUIRED)
include("${CMAKE_CURRENT_SOURCE_DIR}/../cmake/mbedtls.cmake")

if(NOT TARGET agentspeed_quota)
    message(FATAL_ERROR "the relay reads quotas (REMOTE 11): zlib (pkg-config zlib) is required")
endif()

file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../../VERSION" AGENTSPEED_RELAY_VERSION)
string(STRIP "${AGENTSPEED_RELAY_VERSION}" AGENTSPEED_RELAY_VERSION)

add_library(agentspeed_relay STATIC
    src/channel.cpp
    src/history_sync.cpp
    src/history_worker.cpp
    src/json_out.cpp
    src/live_session.cpp
    src/protocol.cpp
    src/serve.cpp
    src/zone_file.cpp
    # Quotas (REMOTE 11)
    src/quota_bridge.cpp
    src/quota_lines.cpp
    src/quota_session.cpp
    src/quota_transport.cpp
    src/net/http_wire.cpp
    src/net/https_client.cpp
    src/net/proxy.cpp
    src/net/socket.cpp
    src/net/tls.cpp
    src/net/url.cpp
)
target_include_directories(agentspeed_relay PUBLIC src)
target_link_libraries(agentspeed_relay PUBLIC agentspeed_base agentspeed_quota agentspeed_mbedtls Threads::Threads)

add_executable(agent-speed-relay src/main.cpp)
target_link_libraries(agent-speed-relay PRIVATE agentspeed_relay)
target_compile_definitions(agent-speed-relay PRIVATE AGENTSPEED_RELAY_VERSION="${AGENTSPEED_RELAY_VERSION}")
if(AGENTSPEED_STATIC)
    target_link_options(agent-speed-relay PRIVATE -static)
endif()
