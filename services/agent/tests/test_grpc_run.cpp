#include "agent_service.hpp"
#include "fake_sender.hpp"
#include "test_util.hpp"

#include <grpcpp/grpcpp.h>
#include "bendmark/v1/agent.grpc.pb.h"

#include <chrono>
#include <memory>
#include <string>
#include <vector>

namespace {

struct TestServer {
    std::unique_ptr<grpc::Server> server;
    std::unique_ptr<AgentServiceImpl> service;
    std::string addr;
};

TestServer start_test_server(std::shared_ptr<AgentCore> core) {
    auto service = std::make_unique<AgentServiceImpl>(core);

    grpc::ServerBuilder builder;
    int port = 0;
    builder.AddListeningPort("127.0.0.1:0", grpc::InsecureServerCredentials(), &port);
    builder.RegisterService(service.get());

    auto server = builder.BuildAndStart();
    std::string addr = "127.0.0.1:" + std::to_string(port);

    return TestServer{std::move(server), std::move(service), addr};
}

void test_two_steps_two_responses() {
    AgentOptions opt;
    opt.timeout_ms = 5000;
    opt.max_concurrency = 1000;

    auto core = std::make_shared<AgentCore>(
        []() { return std::make_shared<FakeSender>(std::chrono::microseconds(100)); },
        opt);

    auto ts = start_test_server(core);
    auto channel = grpc::CreateChannel(ts.addr, grpc::InsecureChannelCredentials());
    auto stub = bendmark::v1::AgentService::NewStub(channel);

    bendmark::v1::RunRequest req;
    req.set_run_id("11111111-1111-1111-1111-111111111111");
    auto* sc = req.mutable_scenario();
    sc->set_name("smoke");
    sc->mutable_target()->set_url("http://fake/");
    sc->mutable_target()->set_method("GET");
    auto* s1 = sc->add_steps();
    s1->set_target_rps(10.0);
    s1->set_duration_seconds(0.2);
    auto* s2 = sc->add_steps();
    s2->set_target_rps(20.0);
    s2->set_duration_seconds(0.2);

    grpc::ClientContext ctx;
    auto reader = stub->Run(&ctx, req);

    std::vector<uint32_t> indexes;
    bendmark::v1::RunResponse resp;
    while (reader->Read(&resp)) {
        indexes.push_back(resp.step_result().index());
    }

    auto status = reader->Finish();
    test::expect(status.ok(), "Run status OK");
    test::expect_eq_long(static_cast<long long>(indexes.size()), 2, "two responses");
    if (indexes.size() == 2) {
        test::expect_eq_long(indexes[0], 1, "first index 1");
        test::expect_eq_long(indexes[1], 2, "second index 2");
    }
}

void test_skipped_step_still_sent() {
    AgentOptions opt;
    opt.timeout_ms = 5000;
    opt.max_concurrency = 1000;
    opt.concurrency_override = 1;

    auto core = std::make_shared<AgentCore>(
        []() { return std::make_shared<FakeSender>(std::chrono::milliseconds(30)); },
        opt);

    auto ts = start_test_server(core);
    auto channel = grpc::CreateChannel(ts.addr, grpc::InsecureChannelCredentials());
    auto stub = bendmark::v1::AgentService::NewStub(channel);

    bendmark::v1::RunRequest req;
    req.set_run_id("22222222-2222-2222-2222-222222222222");
    auto* sc = req.mutable_scenario();
    sc->set_name("skip");
    sc->mutable_target()->set_url("http://fake/");
    sc->mutable_target()->set_method("GET");
    auto* s1 = sc->add_steps();
    s1->set_target_rps(100.0);
    s1->set_duration_seconds(0.3);

    grpc::ClientContext ctx;
    auto reader = stub->Run(&ctx, req);

    bendmark::v1::RunResponse resp;
    bool got = reader->Read(&resp);
    auto status = reader->Finish();

    test::expect(status.ok(), "Run status OK with skips");
    test::expect(got, "one response received");
    if (got) {
        test::expect(resp.step_result().skipped_count() > 0,
                     "skipped_count > 0 is reported");
    }
}

} // namespace

int main() {
    test_two_steps_two_responses();
    test_skipped_step_still_sent();
    return test::finish("test_grpc_run");
}