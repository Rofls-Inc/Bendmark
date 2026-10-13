add_executable(load_agent_tests_scheduler test_scheduler.cpp)
target_link_libraries(load_agent_tests_scheduler PRIVATE load_agent_core)
add_test(NAME scheduler COMMAND load_agent_tests_scheduler)

add_executable(load_agent_tests_scenario test_scenario.cpp)
target_link_libraries(load_agent_tests_scenario PRIVATE load_agent_core)
add_test(NAME scenario COMMAND load_agent_tests_scenario)

add_executable(load_agent_tests_percentiles test_percentiles.cpp)
target_link_libraries(load_agent_tests_percentiles PRIVATE load_agent_core)
add_test(NAME percentiles COMMAND load_agent_tests_percentiles)

add_executable(load_agent_tests_workers test_workers.cpp)
target_link_libraries(load_agent_tests_workers PRIVATE load_agent_core)
add_test(NAME workers COMMAND load_agent_tests_workers)

add_executable(load_agent_tests_grpc_run test_grpc_run.cpp)
target_link_libraries(load_agent_tests_grpc_run PRIVATE
    load_agent_core agent_grpc agent_proto gRPC::grpc++)
add_test(NAME grpc_run COMMAND load_agent_tests_grpc_run)

add_executable(load_agent_tests_grpc_stop test_grpc_stop.cpp)
target_link_libraries(load_agent_tests_grpc_stop PRIVATE
    load_agent_core agent_grpc agent_proto gRPC::grpc++)
add_test(NAME grpc_stop COMMAND load_agent_tests_grpc_stop)

add_executable(load_agent_tests_grpc_errors test_grpc_errors.cpp)
target_link_libraries(load_agent_tests_grpc_errors PRIVATE
    load_agent_core agent_grpc agent_proto gRPC::grpc++)
add_test(NAME grpc_errors COMMAND load_agent_tests_grpc_errors)