find_package(Threads REQUIRED)

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
)
target_include_directories(agentspeed_relay PUBLIC src)
target_link_libraries(agentspeed_relay PUBLIC agentspeed_base Threads::Threads)

add_executable(agent-speed-relay src/main.cpp)
target_link_libraries(agent-speed-relay PRIVATE agentspeed_relay)
target_compile_definitions(agent-speed-relay PRIVATE AGENTSPEED_RELAY_VERSION="${AGENTSPEED_RELAY_VERSION}")
if(AGENTSPEED_STATIC)
    target_link_options(agent-speed-relay PRIVATE -static)
endif()
