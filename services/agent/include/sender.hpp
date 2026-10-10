#pragma once

#include <chrono>
#include <memory>
#include <string>

struct RequestResult {
    bool success = false;
    long latency_us = 0;
    long send_lag_us = 0;
    long http_code = 0;
    std::string error;
};

class Sender {
public:
    virtual ~Sender() = default;

    virtual RequestResult send(const std::string& target,
                               long timeout_ms,
                               std::chrono::steady_clock::time_point scheduled_at) = 0;
};

std::shared_ptr<Sender> make_http_sender();