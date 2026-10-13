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
find_package(Protobuf REQUIRED)
find_package(gRPC CONFIG REQUIRED)

# proto/ лежит в корне репозитория, services/agent/ — на два уровня ниже.
set(PROTO_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../../proto)
if(NOT EXISTS ${PROTO_ROOT}/bendmark/v1/agent.proto)
    message(FATAL_ERROR "agent.proto not found at ${PROTO_ROOT}/bendmark/v1/agent.proto")
endif()

set(GENERATED_DIR ${CMAKE_CURRENT_BINARY_DIR}/generated)
file(MAKE_DIRECTORY ${GENERATED_DIR}/bendmark/v1)

set(PROTO_FILES
    ${PROTO_ROOT}/bendmark/v1/agent.proto
    ${PROTO_ROOT}/bendmark/v1/common.proto
)

set(GENERATED_CC
    ${GENERATED_DIR}/bendmark/v1/agent.pb.cc
    ${GENERATED_DIR}/bendmark/v1/agent.grpc.pb.cc
    ${GENERATED_DIR}/bendmark/v1/common.pb.cc
)

set(GENERATED_H
    ${GENERATED_DIR}/bendmark/v1/agent.pb.h
    ${GENERATED_DIR}/bendmark/v1/agent.grpc.pb.h
    ${GENERATED_DIR}/bendmark/v1/common.pb.h
)

# Помечаем сгенерированные файлы как GENERATED — иначе CMake на этапе
# конфигурации попытается найти их на диске и упадёт с "Cannot find source file".
set_source_files_properties(${GENERATED_CC} PROPERTIES GENERATED TRUE)
set_source_files_properties(${GENERATED_H} PROPERTIES GENERATED TRUE)

add_custom_command(
    OUTPUT ${GENERATED_CC} ${GENERATED_H}
    COMMAND $<TARGET_FILE:protobuf::protoc>
    ARGS
        -I ${PROTO_ROOT}
        --cpp_out=${GENERATED_DIR}
        --grpc_out=${GENERATED_DIR}
        --plugin=protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
        ${PROTO_FILES}
    DEPENDS ${PROTO_FILES} protobuf::protoc gRPC::grpc_cpp_plugin
    COMMENT "Generating protobuf/gRPC code from proto/"
    VERBATIM
)

add_library(agent_proto ${GENERATED_CC})
target_include_directories(agent_proto PUBLIC ${GENERATED_DIR})
target_link_libraries(agent_proto PUBLIC gRPC::grpc++ protobuf::libprotobuf)

add_library(load_agent_core
    src/load_agent.cpp
    src/sender_http.cpp
)
target_include_directories(load_agent_core PUBLIC include)
target_link_libraries(load_agent_core PUBLIC CURL::libcurl Threads::Threads)

add_library(agent_grpc
    src/agent_service.cpp
)
target_include_directories(agent_grpc PUBLIC include)
target_link_libraries(agent_grpc PUBLIC
    load_agent_core
    agent_proto
    gRPC::grpc++
)

add_executable(load_agent src/main.cpp)
target_link_libraries(load_agent PRIVATE load_agent_core agent_grpc agent_proto)

include(CTest)
if(BUILD_TESTING)
    add_subdirectory(tests)
endif()