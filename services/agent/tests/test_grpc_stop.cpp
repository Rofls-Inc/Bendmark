#include "agent_service.hpp"
#include "fake_sender.hpp"
#include "grpc_test_util.hpp"
#include "test_util.hpp"

#include <grpcpp/grpcpp.h>
#include "bendmark/v1/agent.grpc.pb.h"

#include <chrono>
#include <memory>
#include <thread>
#include <vector>

namespace {

void test_stop_mid_step_keeps_only_complete_steps() {
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
    s1->set_duration_seconds(0.3);

    auto* s2 = sc->add_steps();
    s2->set_target_rps(20.0);
    s2->set_duration_seconds(3.0);

    grpc::ClientContext ctx;
    auto reader = stub->Run(&ctx, req);

    grpc::Status stop_status;
    bool stop_was_running = false;

    std::thread stopper([&]() {
        std::this_thread::sleep_for(std::chrono::milliseconds(500));

        bendmark::v1::StopRequest stop_req;
        stop_req.set_run_id(req.run_id());
        bendmark::v1::StopResponse stop_resp;
        grpc::ClientContext stop_ctx;

        stop_status = stub->Stop(&stop_ctx, stop_req, &stop_resp);
        stop_was_running = stop_resp.was_running();
    });

    std::vector<uint32_t> indexes;
    bendmark::v1::RunResponse resp;
    while (reader->Read(&resp)) {
        indexes.push_back(resp.step_result().index());
    }
    auto run_status = reader->Finish();

    stopper.join();

    test::expect(stop_status.ok(), "Stop status OK");
    test::expect(stop_was_running, "Stop reported was_running=true");

    test::expect_eq_long(static_cast<long long>(run_status.error_code()),
                         static_cast<long long>(grpc::StatusCode::CANCELLED),
                         "Run finished with CANCELLED");

    test::expect_eq_long(static_cast<long long>(indexes.size()), 1,
                         "exactly one complete step emitted");
    if (indexes.size() == 1) {
        test::expect_eq_long(indexes[0], 1, "the emitted step is index 1");
    }
}

void test_next_run_accepted_after_stop() {
    AgentOptions opt;
    opt.timeout_ms = 5000;
    opt.max_concurrency = 1000;

    auto core = std::make_shared<AgentCore>(
        []() { return std::make_shared<FakeSender>(std::chrono::milliseconds(20)); },
        opt);

    auto ts = start_test_server(core);
    auto channel = grpc::CreateChannel(ts.addr, grpc::InsecureChannelCredentials());
    auto stub = bendmark::v1::AgentService::NewStub(channel);

    bendmark::v1::RunRequest req1;
    req1.set_run_id("44444444-4444-4444-4444-444444444444");
    auto* sc1 = req1.mutable_scenario();
    sc1->set_name("first");
    sc1->mutable_target()->set_url("http://fake/");
    sc1->mutable_target()->set_method("GET");
    auto* s1 = sc1->add_steps();
    s1->set_target_rps(20.0);
    s1->set_duration_seconds(3.0);

    grpc::ClientContext ctx1;
    auto reader1 = stub->Run(&ctx1, req1);

    grpc::Status stop_status;
    bool stop_was_running = false;

    std::thread stopper([&]() {
        std::this_thread::sleep_for(std::chrono::milliseconds(200));

        bendmark::v1::StopRequest stop_req;
        stop_req.set_run_id(req1.run_id());
        bendmark::v1::StopResponse stop_resp;
        grpc::ClientContext stop_ctx;

        stop_status = stub->Stop(&stop_ctx, stop_req, &stop_resp);
        stop_was_running = stop_resp.was_running();
    });

    bendmark::v1::RunResponse resp1;
    while (reader1->Read(&resp1)) {}
    auto run1_status = reader1->Finish();

    stopper.join();

    test::expect(stop_status.ok(), "Stop status OK");
    test::expect(stop_was_running, "first run was running when Stop arrived");
    test::expect_eq_long(static_cast<long long>(run1_status.error_code()),
                         static_cast<long long>(grpc::StatusCode::CANCELLED),
                         "first run CANCELLED");

    bendmark::v1::RunRequest req2;
    req2.set_run_id("55555555-5555-5555-5555-555555555555");
    auto* sc2 = req2.mutable_scenario();
    sc2->set_name("second");
    sc2->mutable_target()->set_url("http://fake/");
    sc2->mutable_target()->set_method("GET");
    auto* s2 = sc2->add_steps();
    s2->set_target_rps(10.0);
    s2->set_duration_seconds(0.2);

    grpc::ClientContext ctx2;
    auto reader2 = stub->Run(&ctx2, req2);
    bendmark::v1::RunResponse resp2;
    while (reader2->Read(&resp2)) {}
    auto run2_status = reader2->Finish();

    test::expect(run2_status.ok(),
                 "second run accepted after Stop (not RESOURCE_EXHAUSTED)");
}

} // namespace

int main() {
    test_stop_mid_step_keeps_only_complete_steps();
    test_next_run_accepted_after_stop();
    return test::finish("test_grpc_stop");
}