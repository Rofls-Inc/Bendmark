#include "load_agent.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdio>
#include <deque>
#include <fstream>
#include <iostream>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

namespace {

struct RunResult {
    std::vector<RequestResult> requests;
    long long skipped = 0;
};

double percentile_ms(const std::vector<long>& sorted_us, int p_permille) {
    if (sorted_us.empty()) return 0.0;
    size_t n = sorted_us.size();

    size_t rank = (static_cast<size_t>(p_permille) * n + 999) / 1000;
    if (rank == 0) rank = 1;
    size_t idx = rank - 1;

    return sorted_us[idx] / 1000.0;
}

void write_double(std::ostream& os, double v, int precision = 2) {
    char fmt[16];
    std::snprintf(fmt, sizeof(fmt), "%%.%df", precision);

    char buf[64];
    std::snprintf(buf, sizeof(buf), fmt, v);

    std::string s(buf);
    if (s.find('.') != std::string::npos) {
        while (!s.empty() && s.back() == '0') s.pop_back();
        if (!s.empty() && s.back() == '.') s.pop_back();
    }
    os << s;
}

RunResult run_one_step(const std::string& url,
                       const StepConfig& step,
                       const ScenarioConfig& opt,
                       std::shared_ptr<Sender> sender,
                       std::atomic<bool>& stop) {
    RunResult out;
    std::mutex results_mtx;
    std::mutex work_mtx;
    std::condition_variable work_cv;

    std::deque<std::chrono::steady_clock::time_point> ready;
    std::vector<std::thread> workers;
    int outstanding = 0;
    bool stopping = false;

    const int concurrency = compute_workers(step, opt);

    for (int i = 0; i < concurrency; ++i) {
        workers.emplace_back([&, url, timeout = opt.timeout_ms, sender] {
            while (true) {
                std::chrono::steady_clock::time_point scheduled_at;
                {
                    std::unique_lock<std::mutex> lk(work_mtx);
                    work_cv.wait(lk, [&] { return !ready.empty() || stopping; });
                    if (ready.empty()) return;
                    scheduled_at = ready.front();
                    ready.pop_front();
                }

                RequestResult r = sender->send(url, timeout, scheduled_at);

                {
                    std::lock_guard<std::mutex> lk(results_mtx);
                    out.requests.push_back(std::move(r));
                }
                {
                    std::lock_guard<std::mutex> lk(work_mtx);
                    --outstanding;
                }
            }
        });
    }

    const auto interval = std::chrono::duration<double>(1.0 / step.target_rps);
    const auto start_time = std::chrono::steady_clock::now();
    const auto duration = std::chrono::duration<double>(step.duration_seconds);
    const auto end_time = start_time +
        std::chrono::duration_cast<std::chrono::steady_clock::duration>(duration);
    auto next_tick = start_time;

    while (next_tick < end_time) {
        std::this_thread::sleep_until(next_tick);

        bool queued = false;
        {
            std::lock_guard<std::mutex> lk(work_mtx);
            if (outstanding >= concurrency) {
                ++out.skipped;
            } else {
                ++outstanding;
                ready.push_back(next_tick);
                queued = true;
            }
        }
        if (queued) work_cv.notify_one();

        if (stop.load()) break;

        next_tick += std::chrono::duration_cast<std::chrono::steady_clock::duration>(interval);
    }

    {
        std::lock_guard<std::mutex> lk(work_mtx);
        stopping = true;
    }
    work_cv.notify_all();
    for (auto& w : workers) w.join();

    return out;
}

StepResult compute_step_result(int index, const StepConfig& step, const RunResult& run) {
    StepResult s;
    s.index = index;
    s.target_rps = step.target_rps;
    s.duration_seconds = step.duration_seconds;
    s.skipped_count = run.skipped;

    std::vector<long> latencies;
    latencies.reserve(run.requests.size());

    for (const auto& r : run.requests) {
        latencies.push_back(r.latency_us);
        if (!r.success) {
            ++s.error_count;
        }
    }

    s.request_count = static_cast<long long>(latencies.size());

    s.throughput_rps = step.duration_seconds > 0
        ? static_cast<double>(s.request_count) / step.duration_seconds
        : 0.0;

    if (s.request_count > 0) {
        s.error_rate_percent =
            100.0 * static_cast<double>(s.error_count) / static_cast<double>(s.request_count);
    }

    if (!latencies.empty()) {
        std::sort(latencies.begin(), latencies.end());
        s.latency_p50_ms = percentile_ms(latencies, 500);
        s.latency_p90_ms = percentile_ms(latencies, 900);
        s.latency_p99_ms = percentile_ms(latencies, 990);
    }

    return s;
}

} // namespace

int compute_workers(const StepConfig& step, const ScenarioConfig& opt) {
    if (opt.concurrency_override > 0) {
        return opt.concurrency_override;
    }

    double timeout_sec = static_cast<double>(opt.timeout_ms) / 1000.0;
    double need = std::ceil(step.target_rps * timeout_sec) + 1.0;

    if (need < 1.0) need = 1.0;
    if (need > static_cast<double>(opt.max_concurrency)) {
        need = static_cast<double>(opt.max_concurrency);
    }

    return static_cast<int>(need);
}

std::optional<StepResult> run_step(const std::string& url,
                                   const StepConfig& step,
                                   int index,
                                   const ScenarioConfig& opt,
                                   std::shared_ptr<Sender> sender,
                                   std::atomic<bool>& stop) {
    RunResult run = run_one_step(url, step, opt, sender, stop);

    if (stop.load()) {
        return std::nullopt;
    }

    return compute_step_result(index, step, run);
}

std::vector<StepResult> run_scenario(const ScenarioConfig& sc,
                                     std::shared_ptr<Sender> sender,
                                     std::atomic<bool>& stop,
                                     const StepCallback& on_step) {
    std::vector<StepResult> results;
    results.reserve(sc.steps.size());

    for (size_t i = 0; i < sc.steps.size(); ++i) {
        if (stop.load()) break;

        auto r = run_step(sc.url, sc.steps[i], static_cast<int>(i + 1),
                          sc, sender, stop);
        if (!r) break;

        if (on_step) on_step(*r);
        results.push_back(*r);
    }

    return results;
}

bool write_result_json(const std::string& path, const ScenarioResult& result) {
    std::ofstream file;
    std::ostream* out = nullptr;

    if (path == "-") {
        out = &std::cout;
    } else {
        file.open(path);
        if (!file) return false;
        out = &file;
    }

    *out << "{\n";
    *out << "  \"scenario_name\": \"" << result.scenario_name << "\",\n";
    *out << "  \"status\": \"" << result.status << "\",\n";
    *out << "  \"steps\": [\n";

    for (size_t i = 0; i < result.steps.size(); ++i) {
        const auto& s = result.steps[i];

        *out << "    {\n";
        *out << "      \"index\": " << s.index << ",\n";
        *out << "      \"target_rps\": ";       write_double(*out, s.target_rps);       *out << ",\n";
        *out << "      \"duration_seconds\": "; write_double(*out, s.duration_seconds); *out << ",\n";
        *out << "      \"request_count\": "    << s.request_count << ",\n";
        *out << "      \"throughput_rps\": ";   write_double(*out, s.throughput_rps);    *out << ",\n";
        *out << "      \"latency_ms\": { "
             << "\"p50\": "; write_double(*out, s.latency_p50_ms, 3);
        *out << ", \"p90\": "; write_double(*out, s.latency_p90_ms, 3);
        *out << ", \"p99\": "; write_double(*out, s.latency_p99_ms, 3);
        *out << " },\n";
        *out << "      \"errors\": { "
             << "\"count\": " << s.error_count
             << ", \"rate_percent\": "; write_double(*out, s.error_rate_percent, 1);
        *out << " },\n";
        *out << "      \"skipped_count\": " << s.skipped_count << "\n";
        *out << "    }";
        if (i + 1 < result.steps.size()) *out << ",";
        *out << "\n";
    }

    *out << "  ]\n";
    *out << "}\n";

    return static_cast<bool>(*out);
}