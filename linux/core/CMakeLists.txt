find_package(PkgConfig REQUIRED)
pkg_check_modules(ZSTD REQUIRED IMPORTED_TARGET libzstd)
pkg_check_modules(BROTLI REQUIRED IMPORTED_TARGET libbrotlidec)
pkg_check_modules(SQLITE REQUIRED IMPORTED_TARGET sqlite3)
if(AGENTSPEED_STATIC)
    set(OPENSSL_USE_STATIC_LIBS TRUE)
endif()
find_package(OpenSSL REQUIRED COMPONENTS Crypto)
# zlib: the character packs and the quota readers (gzip bodies), which the relay links too (REMOTE 11).
pkg_check_modules(ZLIB IMPORTED_TARGET zlib)
if((AGENTSPEED_BUILD_TESTS OR AGENTSPEED_BUILD_APP OR AGENTSPEED_BUILD_RELAY) AND NOT ZLIB_FOUND)
    message(FATAL_ERROR "zlib (pkg-config zlib) is required for the app, the relay and the tests")
endif()

# JSON and file-system helpers with no library dependency: the base and the status line helper share them.
add_library(agentspeed_json STATIC
    src/fs_util.cpp
    src/json.cpp
)
target_include_directories(agentspeed_json PUBLIC include)

set(AGENTSPEED_PRICES "${CMAKE_CURRENT_SOURCE_DIR}/../../spec/assets/prices.json")
set(AGENTSPEED_PRICES_CPP "${CMAKE_CURRENT_BINARY_DIR}/bundled_prices.cpp")
add_custom_command(
    OUTPUT "${AGENTSPEED_PRICES_CPP}"
    COMMAND ${CMAKE_COMMAND} -DINPUT=${AGENTSPEED_PRICES} -DOUTPUT=${AGENTSPEED_PRICES_CPP}
            -DNAMESPACE=agentspeed::pricing -DFUNCTION=bundledPricesJson -P ${CMAKE_CURRENT_SOURCE_DIR}/../cmake/embed.cmake
    DEPENDS "${AGENTSPEED_PRICES}" "${CMAKE_CURRENT_SOURCE_DIR}/../cmake/embed.cmake"
    VERBATIM)

set(AGENTSPEED_CURRENCIES "${CMAKE_CURRENT_SOURCE_DIR}/../../spec/assets/currencies.json")
set(AGENTSPEED_CURRENCIES_CPP "${CMAKE_CURRENT_BINARY_DIR}/bundled_currencies.cpp")
add_custom_command(
    OUTPUT "${AGENTSPEED_CURRENCIES_CPP}"
    COMMAND ${CMAKE_COMMAND} -DINPUT=${AGENTSPEED_CURRENCIES} -DOUTPUT=${AGENTSPEED_CURRENCIES_CPP}
            -DNAMESPACE=agentspeed::money -DFUNCTION=bundledCurrenciesJson -P ${CMAKE_CURRENT_SOURCE_DIR}/../cmake/embed.cmake
    DEPENDS "${AGENTSPEED_CURRENCIES}" "${CMAKE_CURRENT_SOURCE_DIR}/../cmake/embed.cmake"
    VERBATIM)

# Parsers, monitor, history and prices: everything the relay (REMOTE 3.1) needs, without reporting, the demo or ICU.
add_library(agentspeed_base STATIC
    "${AGENTSPEED_PRICES_CPP}"
    "${AGENTSPEED_CURRENCIES_CPP}"
    src/actions.cpp
    src/antigravity.cpp
    src/board.cpp
    src/claude_parser.cpp
    src/codex_parser.cpp
    src/configured_modes.cpp
    src/dedupe.cpp
    src/devin_database.cpp
    src/devin_history.cpp
    src/devin_message.cpp
    src/devin_paths.cpp
    src/devin_source.cpp
    src/discovery.cpp
    src/droid.cpp
    src/dsh_parser.cpp
    src/engine.cpp
    src/file_reader.cpp
    src/gemini_parser.cpp
    src/grok_parser.cpp
    src/harness.cpp
    src/hermes.cpp
    src/history.cpp
    src/history_analyzer.cpp
    src/history_remote_store.cpp
    src/history_store.cpp
    src/kimi_parser.cpp
    src/line_splitter.cpp
    src/location_editing.cpp
    src/location_lists.cpp
    src/location_resolve.cpp
    src/locations.cpp
    src/model.cpp
    src/modes.cpp
    src/money.cpp
    src/mode_context.cpp
    src/monitor.cpp
    src/monitor_devin.cpp
    src/monitor_qoder.cpp
    src/monitor_hosts.cpp
    src/monitor_remote.cpp
    src/monitor_snapshot.cpp
    src/monitor_sources.cpp
    src/omp_parser.cpp
    src/openclaw.cpp
    src/opencode.cpp
    src/opencode_set.cpp
    src/parents.cpp
    src/path_text.cpp
    src/perf.cpp
    src/perf_history.cpp
    src/pi_parser.cpp
    src/price_book.cpp
    src/price_validator.cpp
    src/pricing.cpp
    src/qoder_calls.cpp
    src/qoder_database.cpp
    src/qoder_history.cpp
    src/qoder_paths.cpp
    src/qoder_source.cpp
    src/report/perf.cpp
    src/report/values.cpp
    src/report/json_writer.cpp
    src/remote/messages.cpp
    src/remote/status.cpp
    src/rfc3339.cpp
    src/set_readouts.cpp
    src/tailer.cpp
    src/traffic.cpp
    src/traffic_linux.cpp
    src/traffic_service.cpp
    src/work_signals.cpp
    src/text.cpp
    src/sqlite.cpp
    src/transcript_parser.cpp
    src/tz.cpp
    src/unpriced.cpp
    src/workbuddy_parser.cpp
    src/zcode.cpp
    src/zcode_set.cpp
    src/zstd_frames.cpp
)
target_include_directories(agentspeed_base PUBLIC include)
target_link_libraries(agentspeed_base PUBLIC agentspeed_json)
if(AGENTSPEED_STATIC)
    target_link_libraries(agentspeed_base PUBLIC ${ZSTD_STATIC_LDFLAGS} ${BROTLI_STATIC_LDFLAGS} ${SQLITE_STATIC_LDFLAGS} OpenSSL::Crypto)
else()
    target_link_libraries(agentspeed_base PUBLIC PkgConfig::ZSTD PkgConfig::BROTLI PkgConfig::SQLITE OpenSSL::Crypto)
endif()

# Quotas (spec/QUOTAS.md). The status line kit is what the helper executable needs (QUOTAS 5.5): JSON and files only,
# so the helper starts fast and loads no library.
add_library(agentspeed_statusline_kit STATIC
    src/quota/cli_quota.cpp
    src/quota/files.cpp
    src/quota/paths.cpp
    src/quota/sources.cpp
    src/quota/statusline_helper.cpp
    src/quota/window.cpp
)
target_link_libraries(agentspeed_statusline_kit PUBLIC agentspeed_json)

# Every quota reader, parser and route, in plain C++ (no Qt): the app links it, and so can the relay (REMOTE 11).
if(ZLIB_FOUND)
add_library(agentspeed_quota STATIC
    src/quota/http.cpp
    # Phase 1 (QUOTAS 2–8)
    src/quota/bridge.cpp
    src/quota/bridge_settings.cpp
    src/quota/codex.cpp
    src/quota/content_decoding.cpp
    src/quota/desktop_entry.cpp
    src/quota/desktop_reader.cpp
    src/quota/desktop_usage.cpp
    src/quota/format.cpp
    src/quota/ordered_json.cpp
    src/quota/presence.cpp
    # Phase 2 (QUOTAS 9)
    src/quota/cookies/browsers.cpp
    src/quota/cookies/crypto.cpp
    src/quota/cookies/header.cpp
    src/quota/cookies/reader.cpp
    src/quota/cookies/stores.cpp
    src/quota/routes/antigravity.cpp
    src/quota/routes/cadence.cpp
    src/quota/routes/credentials.cpp
    src/quota/routes/files.cpp
    src/quota/routes/fingerprint.cpp
    src/quota/routes/local.cpp
    src/quota/routes/requests.cpp
    src/quota/vendor_fetch.cpp
    src/quota/vendor_service.cpp
    src/quota/vendor_service_plans.cpp
    src/quota/vendors/antigravity.cpp
    src/quota/vendors/browser_credits.cpp
    src/quota/vendors/devin.cpp
    src/quota/vendors/droid.cpp
    src/quota/vendors/gemini.cpp
    src/quota/vendors/grok.cpp
    src/quota/vendors/kimi.cpp
    src/quota/vendors/values.cpp
    src/quota/vendors/zai.cpp
)
target_link_libraries(agentspeed_quota PUBLIC agentspeed_base agentspeed_statusline_kit)
if(AGENTSPEED_STATIC)
    target_link_libraries(agentspeed_quota PUBLIC ${ZLIB_STATIC_LDFLAGS})
else()
    target_link_libraries(agentspeed_quota PUBLIC PkgConfig::ZLIB)
endif()
endif()

# The character player (spec/CHARACTERS.md): rig, physics, motion, director, performer and readout layout. Plain C++.
if(AGENTSPEED_BUILD_TESTS OR AGENTSPEED_BUILD_APP)
add_library(agentspeed_character STATIC
    src/character/curve.cpp
    src/character/director.cpp
    src/character/hand_actor.cpp
    src/character/hud.cpp
    src/character/hud_balance.cpp
    src/character/hud_models.cpp
    src/character/hud_quota.cpp
    src/character/hud_wings.cpp
    src/character/motion.cpp
    src/character/motion_profile.cpp
    src/character/moves.cpp
    src/character/pack.cpp
    src/character/param_space.cpp
    src/character/performer.cpp
    src/character/physics.cpp
    src/character/pose_switch.cpp
    src/character/rhythm.cpp
    src/character/rig.cpp
    src/character/selection.cpp
    src/character/springs.cpp
    src/character/catalog.cpp
    src/character/store.cpp
    src/character/zip.cpp
)
target_link_libraries(agentspeed_character PUBLIC agentspeed_base PRIVATE PkgConfig::ZLIB)
endif()

# The desktop app's core: the base plus live reporting and the demo driver.
if(AGENTSPEED_BUILD_TESTS OR AGENTSPEED_BUILD_APP)
    pkg_check_modules(ICU REQUIRED IMPORTED_TARGET icu-uc)
    add_library(agentspeed_core STATIC
        src/demo.cpp
        src/host_selection.cpp
        src/host_subset.cpp
        src/location_entries.cpp
        src/lan/access.cpp
        src/lan/address.cpp
        src/lan/datagram.cpp
        src/lan/feed.cpp
        src/lan/frame.cpp
        src/lan/history.cpp
        src/lan/push.cpp
        src/lan/request_head.cpp
        src/lan/respond.cpp
        src/lan/settings.cpp
        src/lan/websocket.cpp
        src/remote/clock.cpp
        src/remote/connection.cpp
        src/remote/connection_check.cpp
        src/remote/connection_session.cpp
        src/remote/coordinator.cpp
        src/remote/decoder.cpp
        src/remote/decoder_agent.cpp
        src/remote/decoder_history.cpp
        src/remote/decoder_quota.cpp
        src/remote/encoder.cpp
        src/remote/event_loop.cpp
        src/remote/feed.cpp
        src/remote/framer.cpp
        src/remote/history_batch.cpp
        src/remote/history_ledger.cpp
        src/remote/history_rows.cpp
        src/remote/host.cpp
        src/remote/hub.cpp
        src/remote/presentation.cpp
        src/remote/probe.cpp
        src/remote/process.cpp
        src/remote/relay_bundle.cpp
        src/remote/ssh.cpp
        src/remote/zone.cpp
        src/report/crypto.cpp
        src/report/discovery.cpp
        src/report/profile.cpp
        src/report/protocol.cpp
        src/report/reply.cpp
        src/report/scheduler.cpp
        src/report/service.cpp
        src/report/stream.cpp
        src/report/window.cpp
        src/report/body.cpp
        src/report/mode_rows.cpp
        src/report/usernames.cpp
    )
    target_link_libraries(agentspeed_core PUBLIC agentspeed_base agentspeed_quota agentspeed_character PkgConfig::ICU)
endif()
