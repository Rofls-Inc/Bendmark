#pragma once

#include "sender.hpp"

#include <atomic>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

struct StepConfig {
    double target_rps = 0.0;
    double duration_seconds = 0.0;
};

struct ScenarioConfig {
    std::string url;
    std::vector<StepConfig> steps;
    long timeout_ms = 5000;
    int max_concurrency = 1000;
    int concurrency_override = 0;
};

struct StepResult {
    int index = 0;
    double target_rps = 0.0;
    double duration_seconds = 0.0;
    long long request_count = 0;
    double throughput_rps = 0.0;
    double latency_p50_ms = 0.0;
    double latency_p90_ms = 0.0;
    double latency_p99_ms = 0.0;
    long long error_count = 0;
    double error_rate_percent = 0.0;
    long long skipped_count = 0;
};

struct ScenarioResult {
    std::string scenario_name;
    std::string status;
    std::vector<StepResult> steps;
};

using StepCallback = std::function<void(const StepResult&)>;

int compute_workers(const StepConfig& step, const ScenarioConfig& opt);

std::optional<StepResult> run_step(const std::string& url,
                                   const StepConfig& step,
                                   int index,
                                   const ScenarioConfig& opt,
                                   std::shared_ptr<Sender> sender,
                                   std::atomic<bool>& stop);

std::vector<StepResult> run_scenario(const ScenarioConfig& sc,
                                     std::shared_ptr<Sender> sender,
                                     std::atomic<bool>& stop,
                                     const StepCallback& on_step);

bool write_result_json(const std::string& path, const ScenarioResult& result);