# The status line helper (QUOTAS 5.5): JSON and POSIX only, so it starts fast. Its copy runs from <data>/bin, outside
# the AppImage, so the C++ runtime is linked in and only the C library comes from the system.
add_executable(agent-speed-statusline main.cpp)
target_link_libraries(agent-speed-statusline PRIVATE agentspeed_statusline_kit)
if(CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
    target_link_options(agent-speed-statusline PRIVATE -static-libstdc++ -static-libgcc)
endif()
install(TARGETS agent-speed-statusline RUNTIME DESTINATION bin)
