#include "agent_service.hpp"
#include "fake_sender.hpp"
#include "grpc_test_util.hpp"
#include "test_util.hpp"

#include <grpcpp/grpcpp.h>
#include "bendmark/v1/agent.grpc.pb.h"

#include <chrono>
#include <memory>
#include <thread>

namespace {

bendmark::v1::RunRequest make_run(const std::string& run_id,
                                  const std::string& method) {
    bendmark::v1::RunRequest req;
    req.set_run_id(run_id);
    auto* sc = req.mutable_scenario();
    sc->set_name("test");
    sc->mutable_target()->set_url("http://fake/");
    sc->mutable_target()->set_method(method);
    auto* s = sc->add_steps();
    s->set_target_rps(10.0);
    s->set_duration_seconds(0.2);
    return req;
}

grpc::StatusCode run_status(bendmark::v1::AgentService::Stub& stub,
                            const bendmark::v1::RunRequest& req) {
    grpc::ClientContext ctx;
    auto reader = stub.Run(&ctx, req);
    bendmark::v1::RunResponse resp;
    while (reader->Read(&resp)) {}
    return reader->Finish().error_code();
}

void test_invalid_target_rejected() {
    AgentOptions opt;
    opt.timeout_ms = 5000;
    opt.max_concurrency = 1000;

    auto fake = std::make_shared<FakeSender>(std::chrono::microseconds(100));
    auto core = std::make_shared<AgentCore>([fake]() { return fake; }, opt);

    auto ts = start_test_server(core);
    auto channel = grpc::CreateChannel(ts.addr, grpc::InsecureChannelCredentials());
    auto stub = bendmark::v1::AgentService::NewStub(channel);
    const std::string id = "55555555-5555-5555-5555-555555555555";

    auto expect_invalid = [&](const bendmark::v1::RunRequest& req, const std::string& what) {
        test::expect_eq_long(static_cast<long long>(run_status(*stub, req)),
                             static_cast<long long>(grpc::StatusCode::INVALID_ARGUMENT),
                             what + " rejected with INVALID_ARGUMENT");
    };

    for (const std::string method : {"", "get", "HEAD", "FETCH"}) {
        expect_invalid(make_run(id, method), "method '" + method + "'");
    }

    auto get_with_body = make_run(id, "GET");
    get_with_body.mutable_scenario()->mutable_target()->set_body("x");
    expect_invalid(get_with_body, "GET with body");

    auto delete_with_body = make_run(id, "DELETE");
    delete_with_body.mutable_scenario()->mutable_target()->set_body("x");
    expect_invalid(delete_with_body, "DELETE with body");

    auto injected = make_run(id, "POST");
    (*injected.mutable_scenario()->mutable_target()->mutable_headers())["X-A"] = "1\r\nX-Injected: 2";
    expect_invalid(injected, "CRLF in header value");

    auto bad_name = make_run(id, "POST");
    (*bad_name.mutable_scenario()->mutable_target()->mutable_headers())["Bad Name"] = "1";
    expect_invalid(bad_name, "space in header name");

    auto length = make_run(id, "POST");
    (*length.mutable_scenario()->mutable_target()->mutable_headers())["Content-Length"] = "1";
    expect_invalid(length, "Content-Length header");

    auto duplicate = make_run(id, "POST");
    auto& headers = *duplicate.mutable_scenario()->mutable_target()->mutable_headers();
    headers["Accept"] = "a";
    headers["accept"] = "b";
    expect_invalid(duplicate, "headers differing only in case");

    auto big = make_run(id, "POST");
    big.mutable_scenario()->mutable_target()->set_body(std::string(kMaxBodyBytes + 1, 'x'));
    expect_invalid(big, "body over 64 KiB");

    test::expect_eq_long(fake->calls(), 0, "no load for rejected runs");

    for (const std::string method : {"POST", "PUT", "PATCH", "DELETE"}) {
        test::expect(run_status(*stub, make_run(id, method)) == grpc::StatusCode::OK,
                     method + " accepted");
    }
}

void test_second_concurrent_run_busy() {
    AgentOptions opt;
    opt.timeout_ms = 5000;
    opt.max_concurrency = 1000;

    auto core = std::make_shared<AgentCore>(
        []() { return std::make_shared<FakeSender>(std::chrono::milliseconds(50)); },
        opt);

    auto ts = start_test_server(core);
    auto channel = grpc::CreateChannel(ts.addr, grpc::InsecureChannelCredentials());
    auto stub = bendmark::v1::AgentService::NewStub(channel);

    auto req1 = make_run("66666666-6666-6666-6666-666666666666", "GET");
    auto* s = req1.mutable_scenario()->mutable_steps(0);
    s->set_target_rps(20.0);
    s->set_duration_seconds(1.0);

    grpc::ClientContext ctx1;
    auto reader1 = stub->Run(&ctx1, req1);

    std::this_thread::sleep_for(std::chrono::milliseconds(100));

    auto req2 = make_run("77777777-7777-7777-7777-777777777777", "GET");
    grpc::ClientContext ctx2;
    auto reader2 = stub->Run(&ctx2, req2);
    bendmark::v1::RunResponse resp2;
    while (reader2->Read(&resp2)) {}
    auto status2 = reader2->Finish();

    test::expect_eq_long(static_cast<long long>(status2.error_code()),
                         static_cast<long long>(grpc::StatusCode::RESOURCE_EXHAUSTED),
                         "second Run rejected with RESOURCE_EXHAUSTED");

    bendmark::v1::RunResponse resp1;
    while (reader1->Read(&resp1)) {}
    reader1->Finish();
}

void test_stop_bad_uuid() {
    AgentOptions opt;
    auto core = std::make_shared<AgentCore>(
        []() { return std::make_shared<FakeSender>(std::chrono::microseconds(100)); },
        opt);

    auto ts = start_test_server(core);
    auto channel = grpc::CreateChannel(ts.addr, grpc::InsecureChannelCredentials());
    auto stub = bendmark::v1::AgentService::NewStub(channel);

    bendmark::v1::StopRequest req;
    req.set_run_id("not-a-uuid");
    bendmark::v1::StopResponse resp;
    grpc::ClientContext ctx;

    auto status = stub->Stop(&ctx, req, &resp);

    test::expect_eq_long(static_cast<long long>(status.error_code()),
                         static_cast<long long>(grpc::StatusCode::INVALID_ARGUMENT),
                         "bad UUID in Stop rejected with INVALID_ARGUMENT");
    test::expect(!resp.was_running(), "was_running=false for bad UUID");
}

} // namespace

int main() {
    test_invalid_target_rejected();
    test_second_concurrent_run_busy();
    test_stop_bad_uuid();
    return test::finish("test_grpc_errors");
}