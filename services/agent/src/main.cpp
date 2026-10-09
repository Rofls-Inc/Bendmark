#include "load_agent.hpp"
#include "sender.hpp"

#include <curl/curl.h>
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
        cfg.duration_seconds = std::stod(argv[3]);
    } catch (const std::exception& e) {
        std::cerr << "Invalid arguments: " << e.what() << "\n";
        return 1;
    }

    if (cfg.rate_per_sec <= 0 || cfg.duration_seconds <= 0) {
        std::cerr << "rate and duration must be > 0\n";
        return 1;
    }

    std::string json_file;
    try {
        for (int i = 4; i < argc; ++i) {
            std::string a = argv[i];
            if (a == "--concurrency" && i + 1 < argc) {
                cfg.concurrency = std::stoi(argv[++i]);
            } else if (a == "--timeout" && i + 1 < argc) {
                cfg.timeout_ms = std::stol(argv[++i]);
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

    curl_global_init(CURL_GLOBAL_DEFAULT);
    auto sender = make_http_sender();

    LoadGenerator gen(cfg, sender);
    RunResult result = gen.run();

    curl_global_cleanup();

    if (result.requests.empty()) {
        if (result.skipped > 0) {
            std::cerr << "All " << result.skipped << " planned requests were skipped: "
                      << "no free worker at any tick\n";
        } else {
            std::cerr << "No requests were sent\n";
        }
        return 3;
    }

    long success_count = 0;
    long fail_count = 0;
    for (const auto& r : result.requests) {
        if (r.success) ++success_count; else ++fail_count;
    }

    if (result.skipped > 0) {
        long long sent = static_cast<long long>(result.requests.size());
        long long planned = sent + result.skipped;
        std::cerr << "[warn] skipped " << result.skipped << " of " << planned
                  << " planned requests: all " << cfg.concurrency << " workers were busy.\n"
                  << "[warn] The step is not a valid measurement of the service "
                  << "(the analyzer rejects skipped_count > 0). "
                  << "Increase --concurrency to at least rate x latency.\n";
    }

    if (!json_file.empty()) {
        StepStats stats = compute_step_stats(1, cfg, result);
        if (!write_result_json(json_file, stats)) {
            std::cerr << "Cannot write " << json_file << "\n";
            return 1;
        }
        std::cerr << "JSON written to " << json_file << "\n";
    } else {
        std::ostream* out = &std::cout;
        for (const auto& r : result.requests) {
            if (r.success) {
                *out << std::fixed << std::setprecision(3)
                     << (r.latency_us / 1000.0) << "\n";
            } else {
                std::cerr << "request failed: " << r.error
                          << " (after " << std::fixed << std::setprecision(1)
                          << (r.latency_us / 1000.0) << " ms)\n";
            }
        }
    }

    if (success_count == 0) {
        std::cerr << "All " << fail_count << " requests failed\n";
        return 2;
    }

    return 0;
}