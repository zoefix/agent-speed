include(FetchContent)
FetchContent_Declare(googletest
    URL https://github.com/google/googletest/archive/refs/tags/v1.15.2.tar.gz)
set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(googletest)

add_executable(agentspeed_tests
    board_test.cpp
    character_balance_test.cpp
    character_catalog_test.cpp
    character_fixture_test.cpp
    character_models_test.cpp
    character_performer_test.cpp
    character_quota_test.cpp
    character_selection_test.cpp
    character_stretch_test.cpp
    character_wings_test.cpp
    current_values_test.cpp
    devin_fixture_test.cpp
    devin_source_test.cpp
    discovery_test.cpp
    droid_test.cpp
    engine_fixture_test.cpp
    execution_modes_test.cpp
    harness_fixture_test.cpp
    hermes_test.cpp
    history_test.cpp
    host_subset_test.cpp
    json_test.cpp
    lan_address_test.cpp
    lan_frame_test.cpp
    lan_protocol_test.cpp
    lan_settings_test.cpp
    money_test.cpp
    model_attribution_test.cpp
    monitor_test.cpp
    multi_location_test.cpp
    parser_fixture_test.cpp
    perf_score_test.cpp
    pricing_test.cpp
    quota_http_test.cpp
    # Quota phase 1
    quota_bridge_test.cpp
    quota_codex_test.cpp
    quota_desktop_test.cpp
    quota_format_test.cpp
    quota_helper_test.cpp
    quota_presence_test.cpp
    # Quota phase 2
    quota_antigravity_test.cpp
    quota_cookies_test.cpp
    quota_local_routes_test.cpp
    quota_vendor_fixture_test.cpp
    quota_vendor_service_test.cpp
    qoder_fixture_test.cpp
    qoder_source_test.cpp
    report_body_test.cpp
    report_perf_test.cpp
    report_profile_test.cpp
    report_scheduler_test.cpp
    report_service_test.cpp
    report_stream_test.cpp
    report_test.cpp
    remote_cells_test.cpp
    remote_connection_test.cpp
    remote_decoder_test.cpp
    remote_encoder_test.cpp
    remote_feed_test.cpp
    remote_history_test.cpp
    remote_history_zone_test.cpp
    remote_host_test.cpp
    remote_hub_test.cpp
    remote_ingest_test.cpp
    remote_presentation_test.cpp
    remote_process_test.cpp
    remote_quota_test.cpp
    remote_scripts_test.cpp
    remote_ssh_test.cpp
    rfc3339_test.cpp
    set_readouts_test.cpp
    source_locations_test.cpp
    traffic_test.cpp
    unpriced_test.cpp
    work_signals_test.cpp
)
find_package(PkgConfig REQUIRED)
pkg_check_modules(TEST_ZLIB REQUIRED IMPORTED_TARGET zlib)
target_link_libraries(agentspeed_tests PRIVATE agentspeed_core GTest::gtest_main PkgConfig::TEST_ZLIB)
target_compile_definitions(agentspeed_tests PRIVATE
    AGENTSPEED_SPEC_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../../spec")

# QUOTAS 5.5: the status line helper's own tests run the built executable.
if(TARGET agent-speed-statusline)
    target_compile_definitions(agentspeed_tests PRIVATE AGENTSPEED_STATUSLINE_BINARY="$<TARGET_FILE:agent-speed-statusline>")
    add_dependencies(agentspeed_tests agent-speed-statusline)
endif()

# REMOTE 10: the app's connection runs the real relay through a stand-in for ssh.
if(TARGET agent-speed-relay)
    target_compile_definitions(agentspeed_tests PRIVATE AGENTSPEED_RELAY_BINARY="$<TARGET_FILE:agent-speed-relay>")
    add_dependencies(agentspeed_tests agent-speed-relay)
endif()

include(GoogleTest)
gtest_discover_tests(agentspeed_tests)

# Counts heap allocations through a replacement operator new, so it runs in a binary of its own.
add_executable(agentspeed_character_alloc_tests character_alloc_test.cpp)
target_link_libraries(agentspeed_character_alloc_tests PRIVATE agentspeed_character GTest::gtest_main)
target_compile_definitions(agentspeed_character_alloc_tests PRIVATE AGENTSPEED_SPEC_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../../spec")
gtest_discover_tests(agentspeed_character_alloc_tests)

# The app's settings side (DESIGN 5 Storage, METRICS 2.1 Editing) needs Qt: built only with the app.
if(AGENTSPEED_BUILD_APP)
    find_package(Qt6 6.5 REQUIRED COMPONENTS Core DBus Network Widgets)
    add_executable(agentspeed_app_tests
        app/board_fade_test.cpp
        app/character_library_test.cpp
        app/character_quota_test.cpp
        app/character_readings_test.cpp
        app/devin_settings_test.cpp
        app/lan_loopback_test.cpp
        app/orb_character_test.cpp
        app/orb_size_test.cpp
        app/qoder_settings_test.cpp
        app/quota_transport_test.cpp
        app/quota_view_test.cpp
        app/quota_worker_test.cpp
        app/secret_service_test.cpp
        app/source_location_settings_test.cpp
        ../app/src/board/board_store.cpp
        ../app/src/characters/character_library.cpp
        ../app/src/characters/character_transport.cpp
        ../app/src/lan/lan_connection.cpp
        ../app/src/lan/lan_http_post.cpp
        ../app/src/lan/lan_net.cpp
        ../app/src/lan/lan_server.cpp
        ../app/src/lan/lan_targets.cpp
        ../app/src/format.cpp
        ../app/src/orb/character/character_quota.cpp
        ../app/src/orb/character/character_readings.cpp
        ../app/src/orb/idle_fade.cpp
        ../app/src/orbs/orbs_store.cpp
        ../app/src/orbs/size_slider_action.cpp
        ../app/src/preferences.cpp
        ../app/src/quota/quota_text.cpp
        ../app/src/quota/quota_transport.cpp
        ../app/src/quota/quota_view.cpp
        ../app/src/quota/quota_worker.cpp
        ../app/src/quota/secret_service.cpp
        ../app/src/source_locations_view.cpp
        ../app/src/strings.cpp
    )
    set_target_properties(agentspeed_app_tests PROPERTIES AUTOMOC ON)
    # The app's string tables, as the app embeds them, for the texts in every language.
    qt_add_resources(agentspeed_app_tests "test_strings"
        PREFIX "/"
        BASE "${CMAKE_CURRENT_SOURCE_DIR}/../../spec"
        FILES "${CMAKE_CURRENT_SOURCE_DIR}/../../spec/strings.json")
    qt_add_resources(agentspeed_app_tests "test_strings_linux"
        PREFIX "/"
        BASE "${CMAKE_CURRENT_SOURCE_DIR}/../app"
        FILES "${CMAKE_CURRENT_SOURCE_DIR}/../app/strings.linux.json")
    target_include_directories(agentspeed_app_tests PRIVATE ../app/src)
    target_link_libraries(agentspeed_app_tests PRIVATE agentspeed_core Qt6::Core Qt6::DBus Qt6::Gui Qt6::Network Qt6::Widgets GTest::gtest_main PkgConfig::TEST_ZLIB)
    target_compile_definitions(agentspeed_app_tests PRIVATE
        AGENTSPEED_VERSION="${AGENTSPEED_VERSION}"
        AGENTSPEED_SPEC_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../../spec"
        AGENTSPEED_APP_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../app")
    gtest_discover_tests(agentspeed_app_tests)
endif()
