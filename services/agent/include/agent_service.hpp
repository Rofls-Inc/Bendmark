#pragma once

#include "load_agent.hpp"
#include "sender.hpp"

#include <grpcpp/grpcpp.h>
#include "bendmark/v1/agent.grpc.pb.h"

#include <atomic>
#include <functional>
#include <memory>
#include <mutex>
#include <string>

using SenderFactory = std::function<std::shared_ptr<Sender>()>;

struct AgentOptions {
    long timeout_ms = 5000;
    int max_concurrency = 1000;
    int concurrency_override = 0;
};

class AgentCore {
public:
    AgentCore(SenderFactory make_sender, AgentOptions options);

    struct RunOutcome {
        enum class Status { Ok, Cancelled, Busy, Internal };
        Status status = Status::Internal;
        std::string message;
    };

    RunOutcome run(const std::string& run_id,
                   const ScenarioConfig& scenario,
                   const StepCallback& on_step,
                   CancelCheck is_client_cancelled);

    bool stop(const std::string& run_id);

private:
    SenderFactory make_sender_;
    AgentOptions options_;

    std::mutex mu_;
    bool running_ = false;
    std::string active_run_id_;
    std::shared_ptr<std::atomic<bool>> cancel_;
};

class AgentServiceImpl final : public bendmark::v1::AgentService::Service {
public:
    explicit AgentServiceImpl(std::shared_ptr<AgentCore> core);

    grpc::Status Run(grpc::ServerContext* ctx,
                     const bendmark::v1::RunRequest* req,
                     grpc::ServerWriter<bendmark::v1::RunResponse>* writer) override;

    grpc::Status Stop(grpc::ServerContext* ctx,
                      const bendmark::v1::StopRequest* req,
                      bendmark::v1::StopResponse* resp) override;

private:
    std::shared_ptr<AgentCore> core_;
};