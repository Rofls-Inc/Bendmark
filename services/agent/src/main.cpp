#include "load_agent.hpp"

#include <fstream>
#include <iomanip>
#include <iostream>
#include <string>

namespace {

void print_usage(const char* prog) {
    std::cout
        << "Usage: " << prog << " <URL> <rate_per_sec> <duration_sec> [options]\n"
        << "Options:\n"
        << "  --concurrency N   max parallel requests (default 16)\n"
        << "  --timeout MS      per-request timeout in ms (default 5000)\n"
        << "  --output FILE     write latencies to file (ms, one per line)\n"
        << "  --json FILE       write aggregated stats as JSON\n";
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 4) {
        print_usage(argv[0]);
        return 1;
    }

    LoadConfig cfg;
    try {
        cfg.url = argv[1];
        cfg.rate_per_sec = std::stod(argv[2]);
        cfg.duration = std::chrono::seconds(std::stoll(argv[3]));
    } catch (const std::exception& e) {
        std::cerr << "Invalid arguments: " << e.what() << "\n";
        return 1;
    }

    if (cfg.rate_per_sec <= 0 || cfg.duration.count() <= 0) {
        std::cerr << "rate and duration must be > 0\n";
        return 1;
    }

    std::string output_file;
    std::string json_file;
    try {
        for (int i = 4; i < argc; ++i) {
            std::string a = argv[i];
            if (a == "--concurrency" && i + 1 < argc) {
                cfg.concurrency = std::stoi(argv[++i]);
            } else if (a == "--timeout" && i + 1 < argc) {
                cfg.timeout_ms = std::stol(argv[++i]);
            } else if (a == "--output" && i + 1 < argc) {
                output_file = argv[++i];
            } else if (a == "--json" && i + 1 < argc) {
                json_file = argv[++i];
            } else {
                std::cerr << "Unknown option: " << a << "\n";
                print_usage(argv[0]);
                return 1;
            }
        }
    } catch (const std::exception& e) {
        std::cerr << "Invalid option value: " << e.what() << "\n";
        return 1;
    }

    if (cfg.concurrency <= 0 || cfg.timeout_ms <= 0) {
        std::cerr << "concurrency and timeout must be > 0\n";
        return 1;
    }

    LoadGenerator gen(cfg);
    RunResult result = gen.run();

    if (result.requests.empty() && result.skipped == 0) {
        std::cerr << "No requests were sent\n";
        return 3;
    }

    long success_count = 0;
    long fail_count = 0;

    if (!output_file.empty() || json_file.empty()) {
        std::ostream* out = &std::cout;
        std::ofstream file;
        if (!output_file.empty()) {
            file.open(output_file);
            if (!file) {
                std::cerr << "Cannot open " << output_file << "\n";
                return 1;
            }
            out = &file;
        }

        for (const auto& r : result.requests) {
            if (r.success) {
                ++success_count;
                *out << std::fixed << std::setprecision(3)
                     << (r.latency_us / 1000.0) << "\n";
            } else {
                ++fail_count;
                std::cerr << "request failed: " << r.error
                          << " (after " << std::fixed << std::setprecision(1)
                          << (r.latency_us / 1000.0) << " ms)\n";
            }
        }
    } else {
        for (const auto& r : result.requests) {
            if (r.success) {
                ++success_count;
            } else {
                ++fail_count;
                std::cerr << "request failed: " << r.error
                          << " (after " << std::fixed << std::setprecision(1)
                          << (r.latency_us / 1000.0) << " ms)\n";
            }
        }
    }

    if (result.skipped > 0) {
        long long sent = static_cast<long long>(result.requests.size());
        long long planned = sent + result.skipped;
        double actual_rate = cfg.rate_per_sec * static_cast<double>(sent) / planned;
        std::cerr << "[warn] skipped " << result.skipped << " of " << planned
                  << " planned requests; actual rate ~"
                  << std::fixed << std::setprecision(2) << actual_rate
                  << " req/s (requested " << cfg.rate_per_sec << ")\n";
    }

    if (!json_file.empty()) {
        StepStats stats = compute_step_stats(cfg, result);
        if (!write_result_json(json_file, stats)) {
            std::cerr << "Cannot write " << json_file << "\n";
            return 1;
        }
        std::cerr << "JSON written to " << json_file << "\n";
    }

    if (success_count == 0) {
        std::cerr << "All " << fail_count << " requests failed\n";
        return 2;
    }

    return 0;
}
