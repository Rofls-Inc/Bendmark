#include "agent_service.hpp"
#include "fake_sender.hpp"
#include "test_util.hpp"

#include <grpcpp/grpcpp.h>
#include "bendmark/v1/agent.grpc.pb.h"

#include <chrono>
#include <memory>
#include <thread>
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

void test_stop_mid_step_cancels_and_no_partial_step() {
    AgentOptions opt;
    opt.timeout_ms = 5000;
    opt.max_concurrency = 1000;

    auto core = std::make_shared<AgentCore>(
        []() { return std::make_shared<FakeSender>(std::chrono::milliseconds(20)); },
        opt);

    auto ts = start_test_server(core);
    auto channel = grpc::CreateChannel(ts.addr, grpc::InsecureChannelCredentials());
    auto stub = bendmark::v1::AgentService::NewStub(channel);

    bendmark::v1::RunRequest req;
    req.set_run_id("33333333-3333-3333-3333-333333333333");
    auto* sc = req.mutable_scenario();
    sc->set_name("stop-mid");
    sc->mutable_target()->set_url("http://fake/");
    sc->mutable_target()->set_method("GET");
    auto* s1 = sc->add_steps();
    s1->set_target_rps(20.0);
    s1->set_duration_seconds(3.0);

    grpc::ClientContext ctx;
    auto reader = stub->Run(&ctx, req);

    std::thread stopper([&]() {
        std::this_thread::sleep_for(std::chrono::milliseconds(200));

        bendmark::v1::StopRequest stop_req;
        stop_req.set_run_id(req.run_id());
        bendmark::v1::StopResponse stop_resp;
        grpc::ClientContext stop_ctx;

        auto status = stub->Stop(&stop_ctx, stop_req, &stop_resp);
        test::expect(status.ok(), "Stop status OK");
        test::expect(stop_resp.was_running(), "Stop reported was_running=true");
    });

    std::vector<uint32_t> indexes;
    bendmark::v1::RunResponse resp;
    while (reader->Read(&resp)) {
        indexes.push_back(resp.step_result().index());
    }
    auto status = reader->Finish();

    stopper.join();

    test::expect_eq_long(static_cast<long long>(status.error_code()),
                         static_cast<long long>(grpc::StatusCode::CANCELLED),
                         "Run finished with CANCELLED");
    test::expect_eq_long(static_cast<long long>(indexes.size()), 0,
                         "no partial step emitted");
}

void test_stop_after_completion_returns_false() {
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
    req.set_run_id("44444444-4444-4444-4444-444444444444");
    auto* sc = req.mutable_scenario();
    sc->set_name("quick");
    sc->mutable_target()->set_url("http://fake/");
    sc->mutable_target()->set_method("GET");
    auto* s1 = sc->add_steps();
    s1->set_target_rps(10.0);
    s1->set_duration_seconds(0.2);

    grpc::ClientContext run_ctx;
    auto reader = stub->Run(&run_ctx, req);
    bendmark::v1::RunResponse resp;
    while (reader->Read(&resp)) {}
    auto run_status = reader->Finish();
    test::expect(run_status.ok(), "Run finished OK");

    bendmark::v1::StopRequest stop_req;
    stop_req.set_run_id(req.run_id());
    bendmark::v1::StopResponse stop_resp;
    grpc::ClientContext stop_ctx;
    auto stop_status = stub->Stop(&stop_ctx, stop_req, &stop_resp);

    test::expect(stop_status.ok(), "Stop status OK");
    test::expect(!stop_resp.was_running(),
                 "was_running=false for completed run");
}

} // namespace

int main() {
    test_stop_mid_step_cancels_and_no_partial_step();
    test_stop_after_completion_returns_false();
    return test::finish("test_grpc_stop");
}