find_package(Qt6 6.5 REQUIRED COMPONENTS Core Gui Widgets Qml Quick QuickControls2)
qt_standard_project_setup(REQUIRES 6.5)

qt_add_executable(agent-speed
    src/main.cpp
    src/app_state.cpp
    src/flyout.cpp
    src/format.cpp
    src/graphics.cpp
    src/heat.cpp
    src/monitor_worker.cpp
    src/strings.cpp
    src/tray_icon.cpp
)

set(agent_speed_qml
    qml/AgentRow.qml
    qml/Dashboard.qml
    qml/Format.js
    qml/Gauge.qml
    qml/Glyph.qml
    qml/Hairline.qml
    qml/Heat.js
    qml/IconButton.qml
    qml/LiveChart.qml
    qml/LiveSlot.qml
    qml/PhaseBadge.qml
    qml/SectionHeader.qml
    qml/SourceRow.qml
    qml/Sync.js
    qml/Theme.qml
)
# Serve every file from the module root, so the implicit directory import sees the qmldir (and the Theme singleton).
foreach(file IN LISTS agent_speed_qml)
    get_filename_component(alias "${file}" NAME)
    set_source_files_properties("${file}" PROPERTIES QT_RESOURCE_ALIAS "${alias}")
endforeach()
set_source_files_properties(qml/Theme.qml PROPERTIES QT_QML_SINGLETON_TYPE TRUE)

qt_add_qml_module(agent-speed
    URI AgentSpeed
    VERSION 1.0
    QML_FILES ${agent_speed_qml}
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
