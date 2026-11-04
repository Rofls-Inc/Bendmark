#include "agent_service.hpp"
#include "fake_sender.hpp"
#include "grpc_test_util.hpp"
#include "http_test_server.hpp"
#include "test_util.hpp"

#include <grpcpp/grpcpp.h>
#include "bendmark/v1/agent.grpc.pb.h"

#include <curl/curl.h>

#include <chrono>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace {

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

// Сквозной путь: сценарий из gRPC -> HttpSender -> HTTP-сервер.
// Каждый запрос ступени приходит с методом, заголовками и телом из сценария
void test_post_scenario_reaches_server_unchanged() {
    HttpTestServer server;

    AgentOptions opt;
    opt.timeout_ms = 2000;
    opt.max_concurrency = 50;
    auto core = std::make_shared<AgentCore>([]() { return make_http_sender(); }, opt);

    auto ts = start_test_server(core);
    auto channel = grpc::CreateChannel(ts.addr, grpc::InsecureChannelCredentials());
    auto stub = bendmark::v1::AgentService::NewStub(channel);

    const std::string body = "{\"ticker\":\"ABS\",\"qty\":5}";
    bendmark::v1::RunRequest req;
    req.set_run_id("33333333-3333-3333-3333-333333333333");
    auto* sc = req.mutable_scenario();
    sc->set_name("post");
    auto* target = sc->mutable_target();
    target->set_url(server.url("/orders"));
    target->set_method("POST");
    (*target->mutable_headers())["Content-Type"] = "application/json";
    (*target->mutable_headers())["X-Run"] = "bendmark";
    target->set_body(body);
    auto* s1 = sc->add_steps();
    s1->set_target_rps(20.0);
    s1->set_duration_seconds(0.3);

    grpc::ClientContext ctx;
    auto reader = stub->Run(&ctx, req);
    bendmark::v1::RunResponse resp;
    bool got = reader->Read(&resp);
    while (reader->Read(&resp)) {}
    auto status = reader->Finish();

    test::expect(status.ok(), "POST run OK: " + status.error_message());
    test::expect(got, "step result received");
    if (got) {
        test::expect(resp.step_result().request_count() > 0, "requests completed");
        test::expect_eq_long(static_cast<long long>(resp.step_result().errors().count()), 0, "no errors");
    }

    auto requests = server.requests();
    test::expect(!requests.empty(), "server received requests");
    bool all_same = true;
    for (const auto& r : requests) {
        all_same = all_same && r.method == "POST" && r.target == "/orders" && r.body == body
            && r.values("Content-Type") == std::vector<std::string>{"application/json"}
            && r.header("X-Run") == std::optional<std::string>("bendmark");
    }
    test::expect(all_same, "every request has the scenario's method, headers and body");
}

} // namespace

int main() {
    curl_global_init(CURL_GLOBAL_DEFAULT);
    test_post_scenario_reaches_server_unchanged();
    test_two_steps_two_responses();
    test_skipped_step_still_sent();
    curl_global_cleanup();
    return test::finish("test_grpc_run");
}