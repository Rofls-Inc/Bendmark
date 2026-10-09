#include "load_agent.hpp"

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <deque>
#include <fstream>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

namespace {

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

} // namespace

LoadGenerator::LoadGenerator(LoadConfig cfg, std::shared_ptr<Sender> sender)
    : cfg_(std::move(cfg)), sender_(std::move(sender)) {}

RunResult LoadGenerator::run() {
    RunResult out;
    std::mutex results_mtx;
    std::mutex work_mtx;
    std::condition_variable work_cv;

    std::deque<std::chrono::steady_clock::time_point> ready;
    std::vector<std::thread> workers;
    int outstanding = 0;
    bool stopping = false;

    for (int i = 0; i < cfg_.concurrency; ++i) {
        workers.emplace_back([&, url = cfg_.url,
                              timeout = cfg_.timeout_ms,
                              sender = sender_] {
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

    const auto interval = std::chrono::duration<double>(1.0 / cfg_.rate_per_sec);
    const auto start_time = std::chrono::steady_clock::now();
    const auto duration = std::chrono::duration<double>(cfg_.duration_seconds);
    const auto end_time = start_time +
        std::chrono::duration_cast<std::chrono::steady_clock::duration>(duration);
    auto next_tick = start_time;

    while (next_tick < end_time) {
        std::this_thread::sleep_until(next_tick);

        bool queued = false;
        {
            std::lock_guard<std::mutex> lk(work_mtx);
            if (outstanding >= cfg_.concurrency) {
                ++out.skipped;
            } else {
                ++outstanding;
                ready.push_back(next_tick);
                queued = true;
            }
        }
        if (queued) work_cv.notify_one();

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

StepStats compute_step_stats(int index, const LoadConfig& cfg, const RunResult& run) {
    StepStats s;
    s.index = index;
    s.target_rps = cfg.rate_per_sec;
    s.duration_seconds = cfg.duration_seconds;
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

    s.throughput_rps = cfg.duration_seconds > 0
        ? static_cast<double>(s.request_count) / cfg.duration_seconds
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

bool write_result_json(const std::string& path, const StepStats& s) {
    std::ofstream out(path);
    if (!out) return false;

    out << "{\n";
    out << "  \"index\": " << s.index << ",\n";
    out << "  \"target_rps\": ";        write_double(out, s.target_rps);       out << ",\n";
    out << "  \"duration_seconds\": ";  write_double(out, s.duration_seconds); out << ",\n";
    out << "  \"request_count\": "    << s.request_count    << ",\n";
    out << "  \"throughput_rps\": ";  write_double(out, s.throughput_rps);    out << ",\n";
    out << "  \"latency_ms\": { "
        << "\"p50\": "; write_double(out, s.latency_p50_ms, 3);
    out << ", \"p90\": "; write_double(out, s.latency_p90_ms, 3);
    out << ", \"p99\": "; write_double(out, s.latency_p99_ms, 3);
    out << " },\n";
    out << "  \"errors\": { "
        << "\"count\": " << s.error_count
        << ", \"rate_percent\": "; write_double(out, s.error_rate_percent, 1);
    out << " },\n";
    out << "  \"skipped_count\": " << s.skipped_count << "\n";
    out << "}\n";

    return static_cast<bool>(out);
}