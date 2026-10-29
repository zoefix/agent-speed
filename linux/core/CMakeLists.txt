find_package(PkgConfig REQUIRED)
pkg_check_modules(ZSTD REQUIRED IMPORTED_TARGET libzstd)
pkg_check_modules(SQLITE REQUIRED IMPORTED_TARGET sqlite3)
find_package(OpenSSL REQUIRED COMPONENTS Crypto)

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

add_library(agentspeed_core STATIC
    "${AGENTSPEED_PRICES_CPP}"
    "${AGENTSPEED_CURRENCIES_CPP}"
    src/antigravity.cpp
    src/claude_parser.cpp
    src/codex_parser.cpp
    src/dedupe.cpp
    src/demo.cpp
    src/discovery.cpp
    src/droid.cpp
    src/dsh_parser.cpp
    src/engine.cpp
    src/file_reader.cpp
    src/fs_util.cpp
    src/gemini_parser.cpp
    src/grok_parser.cpp
    src/harness.cpp
    src/hermes.cpp
    src/history.cpp
    src/history_analyzer.cpp
    src/history_store.cpp
    src/json.cpp
    src/kimi_parser.cpp
    src/line_splitter.cpp
    src/locations.cpp
    src/model.cpp
    src/modes.cpp
    src/money.cpp
    src/monitor.cpp
    src/monitor_snapshot.cpp
    src/monitor_sources.cpp
    src/omp_parser.cpp
    src/openclaw.cpp
    src/opencode.cpp
    src/parents.cpp
    src/path_text.cpp
    src/pi_parser.cpp
    src/price_book.cpp
    src/price_validator.cpp
    src/pricing.cpp
    src/rfc3339.cpp
    src/tailer.cpp
    src/tasks/command.cpp
    src/tasks/common.cpp
    src/tasks/programs.cpp
    src/tasks/scripts.cpp
    src/tasks/shell_words.cpp
    src/text.cpp
    src/sqlite.cpp
    src/transcript_parser.cpp
    src/tz.cpp
    src/workbuddy_parser.cpp
    src/zcode.cpp
    src/zstd_frames.cpp
)
target_include_directories(agentspeed_core PUBLIC include)
target_link_libraries(agentspeed_core PUBLIC PkgConfig::ZSTD PkgConfig::SQLITE OpenSSL::Crypto)
