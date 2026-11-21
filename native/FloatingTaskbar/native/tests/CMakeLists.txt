add_executable(display_settings_tests display_settings.cpp)
target_link_libraries(display_settings_tests PRIVATE atrium_core_lib)
add_test(NAME display_settings COMMAND display_settings_tests --development)

add_executable(app_processes_tests app_processes.cpp)
target_link_libraries(app_processes_tests PRIVATE atrium_core_lib)
add_test(NAME app_processes COMMAND app_processes_tests)
set_tests_properties(app_processes PROPERTIES TIMEOUT 20)

add_executable(bar_edge_tests bar_edge.cpp)
target_link_libraries(bar_edge_tests PRIVATE atrium_core_lib)
add_test(NAME bar_edge COMMAND bar_edge_tests)

add_executable(near_hover_tests near_hover.cpp)
target_link_libraries(near_hover_tests PRIVATE atrium_core_lib)
add_test(NAME near_hover COMMAND near_hover_tests)

add_executable(hover_wave_tests hover_wave.cpp)
target_link_libraries(hover_wave_tests PRIVATE atrium_core_lib)
add_test(NAME hover_wave COMMAND hover_wave_tests)

add_executable(stage_excluded_tests stage_excluded.cpp)
target_link_libraries(stage_excluded_tests PRIVATE atrium_core_lib)
add_test(NAME stage_excluded COMMAND stage_excluded_tests)

add_executable(tray_interaction_tests tray_interaction.cpp)
target_link_libraries(tray_interaction_tests PRIVATE atrium_core_lib)
add_test(NAME tray_interaction COMMAND tray_interaction_tests)
set_tests_properties(tray_interaction PROPERTIES TIMEOUT 20)

add_executable(quiet_profile_tests quiet_profile.cpp)
target_link_libraries(quiet_profile_tests PRIVATE atrium_core_lib)
add_test(NAME quiet_profile COMMAND quiet_profile_tests)

add_executable(usb_volume_tests usb_volume.cpp)
target_link_libraries(usb_volume_tests PRIVATE atrium_core_lib)
add_test(NAME usb_volume COMMAND usb_volume_tests)
set_tests_properties(usb_volume PROPERTIES TIMEOUT 20)

add_executable(localization_tests localization_checks.cpp)
target_link_libraries(localization_tests PRIVATE atrium_core_lib)
add_test(NAME localization COMMAND localization_tests)
add_custom_command(TARGET localization_tests POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${PROJECT_SOURCE_DIR}/../locales" "$<TARGET_FILE_DIR:localization_tests>/locales"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${PROJECT_SOURCE_DIR}/../tests/fixtures/locales" "$<TARGET_FILE_DIR:localization_tests>/locales")

add_executable(tray_match_tests tray_match.cpp)
target_link_libraries(tray_match_tests PRIVATE atrium_core_lib)
add_test(NAME tray_match COMMAND tray_match_tests)

add_executable(tray_click_tests tray_click.cpp)
target_link_libraries(tray_click_tests PRIVATE atrium_core_lib)
add_test(NAME tray_click COMMAND tray_click_tests)

add_executable(tray_notify_probe tray_notify_probe.cpp)
target_link_libraries(tray_notify_probe PRIVATE atrium_core_lib)

# Manual, read-only by default. Explicit --click uses the actual TrayModel worker.
add_executable(tray_live_probe tray_live_probe.cpp)
target_link_libraries(tray_live_probe PRIVATE atrium_core_lib)

add_executable(tray_protocol_tests tray_protocol.cpp)
target_link_libraries(tray_protocol_tests PRIVATE atrium_core_lib)
add_test(NAME tray_protocol COMMAND tray_protocol_tests)

# Puts a tray host in front of Explorer's for a few seconds: run by hand, with Atrium's host off.
add_executable(tray_host_probe tray_host_probe.cpp)
target_link_libraries(tray_host_probe PRIVATE atrium_core_lib)
add_executable(stage_scroll_tests stage_scroll.cpp)
target_link_libraries(stage_scroll_tests PRIVATE atrium_core_lib)
add_test(NAME stage_scroll COMMAND stage_scroll_tests)

add_executable(messengers_tests messengers.cpp)
target_link_libraries(messengers_tests PRIVATE atrium_core_lib)
add_test(NAME messengers COMMAND messengers_tests)

add_executable(ime_status_tests ime_status.cpp)
target_link_libraries(ime_status_tests PRIVATE atrium_core_lib)
add_test(NAME ime_status COMMAND ime_status_tests)

add_executable(network_monitor_tests network_monitor.cpp)
target_link_libraries(network_monitor_tests PRIVATE atrium_core_lib)
add_test(NAME network_monitor COMMAND network_monitor_tests)

add_executable(panel_fixes_tests panel_fixes.cpp)
target_link_libraries(panel_fixes_tests PRIVATE atrium_core_lib)
add_test(NAME panel_fixes COMMAND panel_fixes_tests)

add_executable(win_key_tests win_key.cpp)
target_link_libraries(win_key_tests PRIVATE atrium_core_lib)
add_test(NAME win_key COMMAND win_key_tests)

add_executable(win32_path_tests win32_path.cpp)
target_link_libraries(win32_path_tests PRIVATE atrium_core_lib)
add_test(NAME win32_path COMMAND win32_path_tests)

add_executable(tray_host_exit_tests tray_host_exit.cpp)
target_link_libraries(tray_host_exit_tests PRIVATE atrium_core_lib)
add_test(NAME tray_host_exit COMMAND tray_host_exit_tests)
set_tests_properties(tray_host_exit PROPERTIES TIMEOUT 15)

add_executable(weather_tests weather.cpp)
target_link_libraries(weather_tests PRIVATE atrium_core_lib)
add_test(NAME weather COMMAND weather_tests)
set_tests_properties(weather PROPERTIES TIMEOUT 20)

add_executable(shell_taskbar_tests shell_taskbar.cpp)
target_link_libraries(shell_taskbar_tests PRIVATE atrium_core_lib)
add_test(NAME shell_taskbar COMMAND shell_taskbar_tests)
set_tests_properties(shell_taskbar PROPERTIES TIMEOUT 15)

add_executable(parsers_tests parsers.cpp)
target_link_libraries(parsers_tests PRIVATE atrium_core_lib)
add_test(NAME parsers COMMAND parsers_tests)
