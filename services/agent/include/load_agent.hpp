#pragma once

#include "sender.hpp"
#include "stats.hpp"

#include <atomic>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

struct ScenarioConfig {
    std::string url;
    std::vector<StepConfig> steps;
    long timeout_ms = 5000;
    int max_concurrency = 1000;
    int concurrency_override = 0;
};

struct ScenarioResult {
    std::string scenario_name;
    std::string status;
    std::vector<StepResult> steps;
};

using StepCallback = std::function<void(const StepResult&)>;
using CancelCheck = std::function<bool()>;

int compute_workers(const StepConfig& step, const ScenarioConfig& opt);

std::optional<StepResult> run_step(const std::string& url,
                                   const StepConfig& step,
                                   int index,
                                   const ScenarioConfig& opt,
                                   std::shared_ptr<Sender> sender,
                                   std::atomic<bool>& stop,
                                   const CancelCheck& external_cancel = {});

std::vector<StepResult> run_scenario(const ScenarioConfig& sc,
                                     std::shared_ptr<Sender> sender,
                                     std::atomic<bool>& stop,
                                     const StepCallback& on_step,
                                     const CancelCheck& external_cancel = {});

bool write_result_json(const std::string& path, const ScenarioResult& result);