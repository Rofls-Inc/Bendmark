#include "agent_service.hpp"

#include <cmath>
#include <stdexcept>

namespace {

bool is_uuid(const std::string& s) {
    if (s.size() != 36) return false;
    for (size_t i = 0; i < s.size(); ++i) {
        char c = s[i];
        if (i == 8 || i == 13 || i == 18 || i == 23) {
            if (c != '-') return false;
        } else {
            bool hex = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
    }
    return true;
}

bool is_http_url(const std::string& s) {
    return s.rfind("http://", 0) == 0 || s.rfind("https://", 0) == 0;
}

} // namespace

AgentCore::AgentCore(SenderFactory make_sender, AgentOptions options)
    : make_sender_(std::move(make_sender)), options_(options) {}

AgentCore::RunOutcome AgentCore::run(const std::string& run_id,
                                     const ScenarioConfig& scenario,
                                     const StepCallback& on_step,
                                     CancelCheck is_client_cancelled) {
    std::shared_ptr<std::atomic<bool>> cancel;
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (running_) {
            return {RunOutcome::Status::Busy, "agent is busy"};
        }
        running_ = true;
        active_run_id_ = run_id;
        cancel = std::make_shared<std::atomic<bool>>(false);
        cancel_ = cancel;
    }

    auto cleanup = [this, run_id]() {
        {
            std::lock_guard<std::mutex> lk(mu_);
            if (active_run_id_ != run_id) return;
            running_ = false;
            active_run_id_.clear();
            cancel_.reset();
        }
        idle_cv_.notify_all();
    };

    try {
        auto sender = make_sender_();

        ScenarioConfig config = scenario;
        config.timeout_ms = options_.timeout_ms;
        config.max_concurrency = options_.max_concurrency;
        config.concurrency_override = options_.concurrency_override;

        auto results = run_scenario(config, sender, *cancel,
                                    on_step, is_client_cancelled);

        cleanup();

        if (cancel->load()) {
            return {RunOutcome::Status::Cancelled, "cancelled"};
        }
        return {RunOutcome::Status::Ok, ""};
    } catch (const std::exception& e) {
        cleanup();
        return {RunOutcome::Status::Internal, e.what()};
    }
}

bool AgentCore::stop(const std::string& run_id) {
    std::unique_lock<std::mutex> lk(mu_);
    if (!running_ || active_run_id_ != run_id) {
        return false;
    }
    cancel_->store(true);
    idle_cv_.wait(lk, [&] { return active_run_id_ != run_id; });
    return true;
}

AgentServiceImpl::AgentServiceImpl(std::shared_ptr<AgentCore> core)
    : core_(std::move(core)) {}

grpc::Status AgentServiceImpl::Run(grpc::ServerContext* ctx,
                                   const bendmark::v1::RunRequest* req,
                                   grpc::ServerWriter<bendmark::v1::RunResponse>* writer) {
    if (!is_uuid(req->run_id())) {
        return {grpc::StatusCode::INVALID_ARGUMENT, "run_id must be a UUID"};
    }

    const auto& sc = req->scenario();
    if (sc.name().empty()) {
        return {grpc::StatusCode::INVALID_ARGUMENT, "scenario name is empty"};
    }
    if (!is_http_url(sc.target().url())) {
        return {grpc::StatusCode::INVALID_ARGUMENT, "target.url must be http/https"};
    }
    if (sc.steps_size() == 0) {
        return {grpc::StatusCode::INVALID_ARGUMENT, "steps must not be empty"};
    }
    if (sc.target().method() != "GET") {
        return {grpc::StatusCode::INVALID_ARGUMENT, "only GET is supported"};
    }
    for (const auto& s : sc.steps()) {
        if (!std::isfinite(s.target_rps()) || s.target_rps() <= 0) {
            return {grpc::StatusCode::INVALID_ARGUMENT, "target_rps must be finite > 0"};
        }
        if (!std::isfinite(s.duration_seconds()) || s.duration_seconds() <= 0) {
            return {grpc::StatusCode::INVALID_ARGUMENT, "duration_seconds must be finite > 0"};
        }
    }

    ScenarioConfig config;
    config.url = sc.target().url();
    for (const auto& s : sc.steps()) {
        config.steps.push_back({s.target_rps(), s.duration_seconds()});
    }

    auto on_step = [&](const StepResult& sr) {
        if (ctx->IsCancelled()) return;

        bendmark::v1::RunResponse resp;
        resp.set_run_id(req->run_id());
        auto* out = resp.mutable_step_result();
        out->set_index(static_cast<uint32_t>(sr.index));
        out->set_target_rps(sr.target_rps);
        out->set_duration_seconds(sr.duration_seconds);
        out->set_request_count(static_cast<uint64_t>(sr.request_count));
        out->set_throughput_rps(sr.throughput_rps);
        out->mutable_latency_ms()->set_p50(sr.latency_p50_ms);
        out->mutable_latency_ms()->set_p90(sr.latency_p90_ms);
        out->mutable_latency_ms()->set_p99(sr.latency_p99_ms);
        out->mutable_errors()->set_count(static_cast<uint64_t>(sr.error_count));
        out->mutable_errors()->set_rate_percent(sr.error_rate_percent);
        out->set_skipped_count(static_cast<uint64_t>(sr.skipped_count));

        writer->Write(resp);
    };

    auto outcome = core_->run(req->run_id(), config, on_step,
                              [ctx]() { return ctx->IsCancelled(); });

    switch (outcome.status) {
        case AgentCore::RunOutcome::Status::Ok:
            return grpc::Status::OK;
        case AgentCore::RunOutcome::Status::Cancelled:
            return {grpc::StatusCode::CANCELLED, outcome.message};
        case AgentCore::RunOutcome::Status::Busy:
            return {grpc::StatusCode::RESOURCE_EXHAUSTED, outcome.message};
        case AgentCore::RunOutcome::Status::Internal:
            return {grpc::StatusCode::INTERNAL, outcome.message};
    }
    return {grpc::StatusCode::INTERNAL, "unknown outcome"};
}

grpc::Status AgentServiceImpl::Stop(grpc::ServerContext*,
                                    const bendmark::v1::StopRequest* req,
                                    bendmark::v1::StopResponse* resp) {
    if (!is_uuid(req->run_id())) {
        return {grpc::StatusCode::INVALID_ARGUMENT, "run_id must be a UUID"};
    }

    bool was = core_->stop(req->run_id());
    resp->set_was_running(was);
    return grpc::Status::OK;
}