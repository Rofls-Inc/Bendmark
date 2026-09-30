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
        << "\nExample:\n"
        << "  " << prog << " https://example.com 50 10 --output out.txt\n";
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
    for (int i = 4; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--concurrency" && i + 1 < argc) {
            cfg.concurrency = std::stoi(argv[++i]);
        } else if (a == "--timeout" && i + 1 < argc) {
            cfg.timeout_ms = std::stol(argv[++i]);
        } else if (a == "--output" && i + 1 < argc) {
            output_file = argv[++i];
        } else {
            std::cerr << "Unknown option: " << a << "\n";
            print_usage(argv[0]);
            return 1;
        }
    }

    LoadGenerator gen(cfg);
    std::vector<RequestResult> results = gen.run();

    if (results.empty()) {
        std::cerr << "No requests were sent\n";
        return 1;
    }

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

    long success_count = 0;
    long fail_count = 0;

    for (const auto& r : results) {
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

    if (success_count == 0) {
        std::cerr << "All " << fail_count << " requests failed\n";
        return 1;
    }

    return 0;
}