cmake_minimum_required(VERSION 3.25)
project(atrium_native VERSION 2.7.0 LANGUAGES CXX RC)
option(ATRIUM_DEVELOPMENT "Run without changing installed-app registration or Windows startup" ON)
option(ATRIUM_ASAN "Build with AddressSanitizer" OFF)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the core starts even if no runtime is installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:/W4;/WX;/permissive-;/utf-8;/DUNICODE;/D_UNICODE>")
# Release keeps its symbols (a .pdb beside the .exe, not shipped), so a hang or a crash can be
# read; the optimisations are the same.
add_compile_options("$<$<AND:$<COMPILE_LANGUAGE:CXX>,$<CONFIG:Release>>:/Zi>")
if(ATRIUM_ASAN)
    add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:/fsanitize=address>")
    add_link_options("$<$<CONFIG:Release>:/DEBUG;/INCREMENTAL:NO;/OPT:NOICF>")
else()
    add_link_options("$<$<CONFIG:Release>:/DEBUG;/OPT:REF;/OPT:ICF>")
endif()

add_subdirectory(core)

option(ATRIUM_BUILD_TESTS "Build native regression checks" OFF)
if(ATRIUM_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif()
