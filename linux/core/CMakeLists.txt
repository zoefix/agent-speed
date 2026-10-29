add_library(agentspeed_core STATIC
    src/json.cpp
    src/rfc3339.cpp
)
target_include_directories(agentspeed_core PUBLIC include)
