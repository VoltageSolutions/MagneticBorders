cmake_minimum_required(VERSION 3.25)
project(MagneticBorders VERSION 0.2.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

option(MB_BUILD_TESTS "Build the unit tests and the fixture runner" ON)
option(MB_WARNINGS_AS_ERRORS "Treat compiler warnings as errors" ON)
option(MB_DETERMINISTIC "Reproducible binaries (/Brepro, /experimental:deterministic)" OFF)

set(MB_CXX_FLAGS "")
set(MB_LINK_FLAGS "")
if(MSVC)
  list(APPEND MB_CXX_FLAGS /W4 /permissive- /utf-8 /EHsc)
  if(MB_WARNINGS_AS_ERRORS)
    list(APPEND MB_CXX_FLAGS /WX)
  endif()
  if(MB_DETERMINISTIC)
    include(CheckCXXCompilerFlag)
    check_cxx_compiler_flag("/experimental:deterministic" MB_HAVE_DETERMINISTIC)
    if(MB_HAVE_DETERMINISTIC)
      list(APPEND MB_CXX_FLAGS /experimental:deterministic)
    endif()
    list(APPEND MB_LINK_FLAGS /Brepro)
  endif()
else()
  list(APPEND MB_CXX_FLAGS -Wall -Wextra)
  if(MB_WARNINGS_AS_ERRORS)
    list(APPEND MB_CXX_FLAGS -Werror)
  endif()
endif()

function(mb_warnings target)
  target_compile_options(${target} PRIVATE ${MB_CXX_FLAGS})
endfunction()

# Executables and DLLs also get the deterministic linker flag.
function(mb_link_flags target)
  if(MB_LINK_FLAGS)
    target_link_options(${target} PRIVATE ${MB_LINK_FLAGS})
  endif()
endfunction()

if(MB_BUILD_TESTS)
  enable_testing()
endif()

# Dependencies are pinned by SHA-256, so a changed or tampered archive fails the configure step.
include(FetchContent)
set(JSON_BuildTests OFF CACHE BOOL "" FORCE)
set(JSON_Install OFF CACHE BOOL "" FORCE)
FetchContent_Declare(nlohmann_json
  URL https://github.com/nlohmann/json/releases/download/v3.11.3/json.tar.xz
  URL_HASH SHA256=d6c65aca6b1ed68e7a182f4757257b107ae403032760ed6ef121c9d55e81757d
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SYSTEM)
FetchContent_MakeAvailable(nlohmann_json)

# Domain (pure) and application layers
add_library(mb_core STATIC core/seam.cpp)
target_include_directories(mb_core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
mb_warnings(mb_core)

# Version header generated from project(VERSION); mb_app and everything that links it can include "mb_version.hpp".
configure_file(host/version.hpp.in ${CMAKE_BINARY_DIR}/generated/mb_version.hpp @ONLY)

add_library(mb_app STATIC app/drag_controller.cpp app/settings.cpp app/pipe_protocol.cpp)
target_include_directories(mb_app PUBLIC ${CMAKE_BINARY_DIR}/generated)
target_link_libraries(mb_app PUBLIC mb_core nlohmann_json::nlohmann_json)
mb_warnings(mb_app)

if(MB_BUILD_TESTS)
  # Fixture runner (no OS dependencies)
  add_library(mb_fixture STATIC fixture-runner/fixture.cpp)
  target_include_directories(mb_fixture PUBLIC fixture-runner)
  target_link_libraries(mb_fixture PUBLIC mb_core)
  mb_warnings(mb_fixture)

  add_executable(fixture_runner fixture-runner/runner_main.cpp)
  target_link_libraries(fixture_runner PRIVATE mb_fixture)
  mb_warnings(fixture_runner)
  mb_link_flags(fixture_runner)
  set(MB_FIXTURES_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../../../tests/fixtures)
  add_test(NAME fixtures COMMAND fixture_runner ${MB_FIXTURES_DIR})

  # Unit tests
  set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
  set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
  FetchContent_Declare(googletest
    URL https://github.com/google/googletest/archive/refs/tags/v1.15.2.zip
    URL_HASH SHA256=f179ec217f9b3b3f3c6e8b02d3e7eda997b49e4ce26d6b235c9053bec9c0bf9f
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    SYSTEM)
  FetchContent_MakeAvailable(googletest)

  set(MB_TEST_SOURCES
    tests/core_tests.cpp tests/controller_tests.cpp tests/settings_tests.cpp
    tests/fixture_tests.cpp)
  if(WIN32)
    list(APPEND MB_TEST_SOURCES tests/win32_tests.cpp host/settings.cpp)
  endif()
  add_executable(mb_tests ${MB_TEST_SOURCES})
  target_link_libraries(mb_tests PRIVATE mb_app mb_fixture GTest::gtest_main)
  target_compile_definitions(mb_tests PRIVATE MB_FIXTURES_DIR="${MB_FIXTURES_DIR}")
  mb_warnings(mb_tests)
  mb_link_flags(mb_tests)
  include(GoogleTest)
  gtest_discover_tests(mb_tests DISCOVERY_MODE PRE_TEST)
endif()

# Win32 adapters and host
if(WIN32)
  enable_language(RC)
  add_library(mb_win32 STATIC
    win32/win_common.cpp
    win32/win32_inventory.cpp
    win32/win32_mover.cpp
    win32/win32_event_source.cpp
    win32/win32_timer.cpp
    win32/paths.cpp
    win32/file_log.cpp
    win32/settings_watcher.cpp
    win32/pipe_server.cpp
    win32/message_window.cpp
    win32/tray_icon.cpp
    win32/run_key.cpp)
  target_link_libraries(mb_win32 PUBLIC mb_app PRIVATE dwmapi user32 shell32 advapi32)
  mb_warnings(mb_win32)

  if(MB_BUILD_TESTS)
    target_link_libraries(mb_tests PRIVATE mb_win32)
  endif()

  # assets/icon.ico is copied into the build dir and embedded as the exe icon (resource id 1).
  configure_file(${CMAKE_CURRENT_SOURCE_DIR}/../../common/assets/icon.ico ${CMAKE_BINARY_DIR}/icon.ico COPYONLY)
  set(MB_ICON_PATH ${CMAKE_BINARY_DIR}/icon.ico)
  configure_file(host/app.rc.in ${CMAKE_BINARY_DIR}/app.rc @ONLY)

  add_executable(magnetic_borders
    host/main.cpp host/engine_app.cpp host/settings.cpp
    host/app.manifest ${CMAKE_BINARY_DIR}/app.rc)
  target_link_libraries(magnetic_borders PRIVATE mb_win32 shell32 user32 advapi32)
  set_target_properties(magnetic_borders PROPERTIES OUTPUT_NAME MagneticBorders.Cpp)
  if(MSVC)
    # Windows subsystem (no console window) while keeping a plain main().
    target_link_options(magnetic_borders PRIVATE /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup)
  endif()
  mb_warnings(magnetic_borders)
  mb_link_flags(magnetic_borders)
  install(TARGETS magnetic_borders RUNTIME DESTINATION .)
endif()
