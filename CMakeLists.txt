cmake_minimum_required(VERSION 3.18)
project(TJRemoteAccess LANGUAGES CXX VERSION 0.1.0)

option(TJRA_BUILD_TESTS "Build the portable session policy tests" ON)

add_library(TJRemoteAccess.Core STATIC
    src/TJRemoteAccess.Core/session_registry.cpp
    src/TJRemoteAccess.Core/session_registry.hpp)
target_include_directories(TJRemoteAccess.Core PUBLIC src/TJRemoteAccess.Core)
target_compile_features(TJRemoteAccess.Core PUBLIC cxx_std_17)
if(MSVC)
    target_compile_options(TJRemoteAccess.Core PRIVATE /W4 /WX)
else()
    target_compile_options(TJRemoteAccess.Core PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

if(TJRA_BUILD_TESTS)
    enable_testing()
    find_package(Threads REQUIRED)
    add_executable(TJRemoteAccess.Core.Tests tests/session_registry_tests.cpp)
    target_link_libraries(TJRemoteAccess.Core.Tests PRIVATE TJRemoteAccess.Core Threads::Threads)
    target_compile_features(TJRemoteAccess.Core.Tests PRIVATE cxx_std_17)
    add_test(NAME session_registry COMMAND TJRemoteAccess.Core.Tests)
endif()
