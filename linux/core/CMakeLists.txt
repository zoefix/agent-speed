find_package(PkgConfig REQUIRED)
pkg_check_modules(ZSTD REQUIRED IMPORTED_TARGET libzstd)
pkg_check_modules(SQLITE REQUIRED IMPORTED_TARGET sqlite3)

add_library(agentspeed_core STATIC
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
    src/json.cpp
    src/kimi_parser.cpp
    src/line_splitter.cpp
    src/locations.cpp
    src/model.cpp
    src/modes.cpp
    src/monitor.cpp
    src/monitor_snapshot.cpp
    src/monitor_sources.cpp
    src/omp_parser.cpp
    src/openclaw.cpp
    src/opencode.cpp
    src/parents.cpp
    src/path_text.cpp
    src/pi_parser.cpp
    src/rfc3339.cpp
    src/tailer.cpp
    src/text.cpp
    src/sqlite.cpp
    src/transcript_parser.cpp
    src/tz.cpp
    src/workbuddy_parser.cpp
    src/zcode.cpp
    src/zstd_frames.cpp
)
target_include_directories(agentspeed_core PUBLIC include)
target_link_libraries(agentspeed_core PUBLIC PkgConfig::ZSTD PkgConfig::SQLITE)
