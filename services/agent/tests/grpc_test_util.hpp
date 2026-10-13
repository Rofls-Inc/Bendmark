#pragma once

#include "agent_service.hpp"

#include <grpcpp/grpcpp.h>
#include "bendmark/v1/agent.grpc.pb.h"

#include <memory>
#include <string>

struct TestServer {
    std::unique_ptr<AgentServiceImpl> service;
    std::unique_ptr<grpc::Server> server;
    std::string addr;
};

inline TestServer start_test_server(std::shared_ptr<AgentCore> core) {
    auto service = std::make_unique<AgentServiceImpl>(core);

    grpc::ServerBuilder builder;
    int port = 0;
    builder.AddListeningPort("127.0.0.1:0", grpc::InsecureServerCredentials(), &port);
    builder.RegisterService(service.get());

    auto server = builder.BuildAndStart();
    std::string addr = "127.0.0.1:" + std::to_string(port);

    return TestServer{std::move(service), std::move(server), addr};
}