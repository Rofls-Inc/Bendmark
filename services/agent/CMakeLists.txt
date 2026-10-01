cmake_minimum_required(VERSION 3.16)
project(load_agent LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

find_package(CURL REQUIRED)
find_package(Threads REQUIRED)

add_executable(load_agent
    src/main.cpp
    src/load_agent.cpp
)

target_include_directories(load_agent PRIVATE include)
target_link_libraries(load_agent PRIVATE CURL::libcurl Threads::Threads)