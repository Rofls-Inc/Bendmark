#pragma once

#include <deque>
#include <string>
#include <vector>

struct LoadConfig {
    std::string url;
    double rate_per_sec = 1.0;
    double duration_seconds = 10.0;
    int concurrency = 16;
    long timeout_ms = 5000;
};

struct RequestResult {
    bool success = false;
    long latency_us = 0;
    long send_lag_us = 0;
    long http_code = 0;
    std::string error;
};

struct RunResult {
    std::vector<RequestResult> requests;
    long long skipped = 0;
};

class LoadGenerator {
public:
    explicit LoadGenerator(LoadConfig cfg);

    RunResult run();

private:
    LoadConfig cfg_;
};

struct StepStats {
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

StepStats compute_step_stats(int index, const LoadConfig& cfg, const RunResult& run);

bool write_result_json(const std::string& path, const StepStats& stats);