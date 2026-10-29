find_package(PkgConfig REQUIRED)
pkg_check_modules(ZSTD REQUIRED IMPORTED_TARGET libzstd)

add_library(agentspeed_core STATIC
    src/claude_parser.cpp
    src/codex_parser.cpp
    src/dedupe.cpp
    src/demo.cpp
    src/engine.cpp
    src/file_reader.cpp
    src/json.cpp
    src/line_splitter.cpp
    src/model.cpp
    src/modes.cpp
    src/monitor.cpp
    src/parents.cpp
    src/path_text.cpp
    src/rfc3339.cpp
    src/transcript_parser.cpp
)
target_include_directories(agentspeed_core PUBLIC include)
target_link_libraries(agentspeed_core PUBLIC PkgConfig::ZSTD)
