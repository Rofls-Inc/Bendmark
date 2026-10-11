#pragma once

#include "sender.hpp"

#include <vector>

struct StepConfig {
    double target_rps = 0.0;
    double duration_seconds = 0.0;
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

struct RequestResult {
    bool success = false;
    long latency_us = 0;
    long http_code = 0;
    std::string error;
};

struct RunResult {
    std::vector<RequestResult> requests;
    long long skipped = 0;
};

double percentile_ms(const std::vector<long>& sorted_us, int p_permille);

StepResult compute_step_result(int index, const StepConfig& step, const RunResult& run);