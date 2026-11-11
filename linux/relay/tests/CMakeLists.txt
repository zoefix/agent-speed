include(GoogleTest)

add_executable(agentspeed_relay_tests
    channel_test.cpp
    end_to_end_test.cpp
    fake_home.cpp
    history_sync_test.cpp
    protocol_test.cpp
    relay_process.cpp
    serve_test.cpp
    zone_test.cpp
    # Quotas (REMOTE 11)
    http_wire_test.cpp
    https_client_test.cpp
    net_stub.cpp
    quota_executable_test.cpp
    quota_protocol_test.cpp
    quota_session_test.cpp
)
# The app's core: every quota line the relay writes must pass the app's own decoder (REMOTE 5.6); OpenSSL comes with it.
target_link_libraries(agentspeed_relay_tests PRIVATE agentspeed_relay agentspeed_core GTest::gtest_main)
target_include_directories(agentspeed_relay_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../../tests")
target_compile_definitions(agentspeed_relay_tests PRIVATE
    AGENTSPEED_SPEC_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../../../spec"
    AGENTSPEED_RELAY_BINARY="$<TARGET_FILE:agent-speed-relay>")
add_dependencies(agentspeed_relay_tests agent-speed-relay)

gtest_discover_tests(agentspeed_relay_tests)
