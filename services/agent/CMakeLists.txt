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

add_library(load_agent_core
    src/load_agent.cpp
    src/sender_http.cpp
)
target_include_directories(load_agent_core PUBLIC include)
target_link_libraries(load_agent_core PUBLIC CURL::libcurl Threads::Threads)

add_executable(load_agent src/main.cpp)
target_link_libraries(load_agent PRIVATE load_agent_core)