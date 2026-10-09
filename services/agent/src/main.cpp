#include "load_agent.hpp"
#include "sender.hpp"

#include <curl/curl.h>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

void print_usage(const char* prog) {
    std::cout
        << "Usage:\n"
        << "  " << prog << " <URL> <rate_per_sec> <duration_sec> [options]\n"
        << "  " << prog << " <URL> --steps RATE:DURATION[,RATE:DURATION...] [options]\n"
        << "Options:\n"
        << "  --steps LIST      scenario steps, e.g. 100:45,200:45.5\n"
        << "  --name NAME       scenario name (default cli)\n"
        << "  --concurrency N   number of workers (default 16)\n"
        << "  --timeout MS      per-request timeout in ms (default 5000)\n"
        << "  --json FILE       write result.json (default stdout)\n";
}

std::vector<StepConfig> parse_steps(const std::string& s) {
    std::vector<StepConfig> steps;
    std::stringstream ss(s);
    std::string pair;
    while (std::getline(ss, pair, ',')) {
        if (pair.empty()) continue;
        auto colon = pair.find(':');
        if (colon == std::string::npos) {
            throw std::runtime_error("bad step (expected RATE:DURATION): " + pair);
        }
        StepConfig step;
        step.target_rps = std::stod(pair.substr(0, colon));
        step.duration_seconds = std::stod(pair.substr(colon + 1));
        if (step.target_rps <= 0 || step.duration_seconds <= 0) {
            throw std::runtime_error("step values must be > 0: " + pair);
        }
        steps.push_back(step);
    }
    if (steps.empty()) {
        throw std::runtime_error("--steps is empty");
    }
    return steps;
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 3) {
        print_usage(argv[0]);
        return 1;
    }

    std::string url = argv[1];
    std::string steps_arg;
    std::string name = "cli";
    int concurrency = 16;
    long timeout_ms = 5000;
    std::string json_file;

    bool positional_done = false;
    double pos_rate = 0.0;
    double pos_duration = 0.0;

    try {
        int i = 2;
        if (i < argc && argv[i][0] != '-') {
            pos_rate = std::stod(argv[i++]);
            if (i < argc && argv[i][0] != '-') {
                pos_duration = std::stod(argv[i++]);
            }
            positional_done = true;
        }

        for (; i < argc; ++i) {
            std::string a = argv[i];
            if (a == "--steps" && i + 1 < argc) {
                steps_arg = argv[++i];
            } else if (a == "--name" && i + 1 < argc) {
                name = argv[++i];
            } else if (a == "--concurrency" && i + 1 < argc) {
                concurrency = std::stoi(argv[++i]);
            } else if (a == "--timeout" && i + 1 < argc) {
                timeout_ms = std::stol(argv[++i]);
            } else if (a == "--json" && i + 1 < argc) {
                json_file = argv[++i];
            } else {
                std::cerr << "Unknown option: " << a << "\n";
                print_usage(argv[0]);
                return 1;
            }
        }
    } catch (const std::exception& e) {
        std::cerr << "Invalid arguments: " << e.what() << "\n";
        return 1;
    }

    ScenarioConfig sc;
    sc.url = url;
    sc.concurrency = concurrency;
    sc.timeout_ms = timeout_ms;

    try {
        if (!steps_arg.empty()) {
            sc.steps = parse_steps(steps_arg);
        } else if (positional_done && pos_rate > 0 && pos_duration > 0) {
            sc.steps.push_back(StepConfig{pos_rate, pos_duration});
        } else {
            std::cerr << "Either positional <rate> <duration> or --steps is required\n";
            print_usage(argv[0]);
            return 1;
        }
    } catch (const std::exception& e) {
        std::cerr << "Invalid steps: " << e.what() << "\n";
        return 1;
    }

    if (sc.concurrency <= 0 || sc.timeout_ms <= 0) {
        std::cerr << "concurrency and timeout must be > 0\n";
        return 1;
    }

    curl_global_init(CURL_GLOBAL_DEFAULT);
    auto sender = make_http_sender();

    std::atomic<bool> stop{false};
    std::vector<StepResult> steps = run_scenario(sc, sender, stop, nullptr);

    curl_global_cleanup();

    ScenarioResult result;
    result.scenario_name = name;
    result.status = "completed";
    result.steps = steps;

    if (!json_file.empty()) {
        if (!write_result_json(json_file, result)) {
            std::cerr << "Cannot write " << json_file << "\n";
            return 1;
        }
        std::cerr << "JSON written to " << json_file << "\n";
    } else {
        write_result_json("-", result);
    }

    // Отчёт о пропусках — в stderr, чтобы не мешать JSON в stdout.
    long long total_skipped = 0;
    for (const auto& s : steps) total_skipped += s.skipped_count;
    if (total_skipped > 0) {
        std::cerr << "[warn] " << total_skipped
                  << " planned requests were skipped across steps: "
                  << "not enough workers. Increase --concurrency.\n";
        std::cerr << "[warn] The analyzer rejects steps with skipped_count > 0.\n";
    }

    return 0;
}