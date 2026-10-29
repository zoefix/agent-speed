find_package(Qt6 6.5 REQUIRED COMPONENTS Core Gui Widgets Qml Quick QuickControls2)
qt_standard_project_setup(REQUIRES 6.5)

qt_add_executable(agent-speed
    src/main.cpp
    src/app_state.cpp
    src/format.cpp
    src/monitor_worker.cpp
    src/strings.cpp
    src/tray_icon.cpp
)

qt_add_qml_module(agent-speed
    URI AgentSpeed
    VERSION 1.0
    QML_FILES qml/Dashboard.qml qml/StatTile.qml
)

qt_add_resources(agent-speed "strings"
    PREFIX "/"
    BASE "${CMAKE_CURRENT_SOURCE_DIR}/../../spec"
    FILES "${CMAKE_CURRENT_SOURCE_DIR}/../../spec/strings.json"
)

target_link_libraries(agent-speed PRIVATE
    agentspeed_core Qt6::Core Qt6::Gui Qt6::Widgets Qt6::Qml Qt6::Quick Qt6::QuickControls2)

install(TARGETS agent-speed RUNTIME DESTINATION bin)
install(FILES packaging/agent-speed.desktop DESTINATION share/applications)
install(FILES packaging/agent-speed.svg DESTINATION share/icons/hicolor/scalable/apps)
