#pragma once

#include <atomic>
#include <memory>
#include <string>

struct SendOutcome {
    bool success = false;
    long http_code = 0;
    std::string error;
};

class Sender {
public:
    virtual ~Sender() = default;

    virtual SendOutcome send(const std::string& target,
                             long timeout_ms,
                             std::atomic<bool>& stop) = 0;
};

std::shared_ptr<Sender> make_http_sender();