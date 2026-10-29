find_package(Qt6 6.5 REQUIRED COMPONENTS Core Gui Network OpenGL WebSockets Widgets Qml Quick QuickControls2)
find_package(PkgConfig REQUIRED)
pkg_check_modules(XCB REQUIRED IMPORTED_TARGET xcb xcb-shape)
qt_standard_project_setup(REQUIRES 6.5)

qt_add_executable(agent-speed
    src/main.cpp
    src/app_state.cpp
    src/flyout.cpp
    src/history_service.cpp
    src/format.cpp
    src/graphics.cpp
    src/heat.cpp
    src/monitor_worker.cpp
    src/network_service.cpp
    src/catalog.cpp
    src/preferences.cpp
    src/report/report_account.cpp
    src/report/report_controller.cpp
    src/report/report_delivery.cpp
    src/report/report_http.cpp
    src/report/report_keys.cpp
    src/report/report_preview.cpp
    src/report/report_settings.cpp
    src/report/report_socket.cpp
    src/report/report_stream_run.cpp
    src/orb/body.cpp
    src/orb/color.cpp
    src/orb/gauge.cpp
    src/orb/led_math.cpp
    src/orb/led_style.cpp
    src/orb/orb_window.cpp
    src/orb/readout.cpp
    src/orb/renderer.cpp
    src/orb/scene.cpp
    src/orb/shaders.cpp
    src/orb/x11.cpp
    src/strings.cpp
    src/task_card.cpp
    src/tray_icon.cpp
)

set(agent_speed_qml
    qml/AboutPage.qml
    qml/ApiPage.qml
    qml/FieldInput.qml
    qml/LinkButton.qml
    qml/ReportConsentWindow.qml
    qml/ReportInviteCard.qml
    qml/ReportPreviewText.qml
    qml/ReportPreviewWindow.qml
    qml/AgentRow.qml
    qml/CostPage.qml
    qml/CurrencyPicker.qml
    qml/Dashboard.qml
    qml/Dropdown.qml
    qml/EffectsPage.qml
    qml/EffortCapsule.qml
    qml/GeneralPage.qml
    qml/OrbPage.qml
    qml/PillButton.qml
    qml/RedlineSlider.qml
    qml/Segmented.qml
    qml/SettingsGroup.qml
    qml/SettingsPage.qml
    qml/SettingsRow.qml
    qml/SettingsWindow.qml
    qml/SourcesPage.qml
    qml/SummaryCard.qml
    qml/TaskBar.qml
    qml/TaskModelTable.qml
    qml/TaskSide.qml
    qml/TasksSection.qml
    qml/TrafficPage.qml
    qml/TrafficSection.qml
    qml/UpdateWindow.qml
    qml/ToggleSwitch.qml
    qml/Format.js
    qml/Gauge.qml
    qml/Glyph.qml
    qml/Hairline.qml
    qml/Heat.js
    qml/History.js
    qml/HistoryChart.qml
    qml/HistoryWindow.qml
    qml/IconButton.qml
    qml/IqCell.qml
    qml/LiveChart.qml
    qml/LiveSlot.qml
    qml/PhaseBadge.qml
    qml/ProofDots.qml
    qml/ScoreCard.qml
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
    agentspeed_core Qt6::Core Qt6::Gui Qt6::Network Qt6::OpenGL Qt6::WebSockets Qt6::Widgets Qt6::Qml Qt6::Quick Qt6::QuickControls2 PkgConfig::XCB)
target_include_directories(agent-speed PRIVATE src)
# The release version (a tag v1.6.0 builds 1.6.0); local builds keep the project's.
set(AGENTSPEED_VERSION "${PROJECT_VERSION}" CACHE STRING "The version the app reports")
target_compile_definitions(agent-speed PRIVATE AGENTSPEED_VERSION="${AGENTSPEED_VERSION}")

install(TARGETS agent-speed RUNTIME DESTINATION bin)
install(FILES packaging/agent-speed.desktop DESTINATION share/applications)
install(FILES packaging/agent-speed.svg DESTINATION share/icons/hicolor/scalable/apps)
