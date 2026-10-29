include(FetchContent)
FetchContent_Declare(googletest
    URL https://github.com/google/googletest/archive/refs/tags/v1.15.2.tar.gz)
set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(googletest)

add_executable(agentspeed_tests
    droid_test.cpp
    engine_fixture_test.cpp
    harness_fixture_test.cpp
    hermes_test.cpp
    json_test.cpp
    parser_fixture_test.cpp
    rfc3339_test.cpp
)
target_link_libraries(agentspeed_tests PRIVATE agentspeed_core GTest::gtest_main)
target_compile_definitions(agentspeed_tests PRIVATE
    AGENTSPEED_SPEC_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../../spec")

include(GoogleTest)
gtest_discover_tests(agentspeed_tests)
