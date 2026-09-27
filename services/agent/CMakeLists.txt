cmake_minimum_required(VERSION 3.21)
project(load_tester CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

find_package(CURL REQUIRED)

add_executable(load_tester main.cpp)
target_link_libraries(load_tester PRIVATE CURL::libcurl)

if(WIN32)
    add_custom_command(TARGET load_tester POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            $<TARGET_RUNTIME_DLLS:load_tester>
            $<TARGET_FILE_DIR:load_tester>
        COMMAND_EXPAND_LISTS
    )
endif()