#pragma once

#include <chrono>
#include <string>
#include <vector>

struct LoadConfig {
    std::string url;
    double rate_per_sec = 1.0;
    std::chrono::seconds duration{10};
    int concurrency = 16;
    long timeout_ms = 5000;
};

class LoadGenerator {
public:
    explicit LoadGenerator(LoadConfig cfg);

    std::vector<long> run();

private:
    LoadConfig cfg_;
};