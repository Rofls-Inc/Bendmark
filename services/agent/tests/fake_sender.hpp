#pragma once

#include "sender.hpp"

#include <atomic>
#include <chrono>
#include <thread>

class FakeSender : public Sender {
public:
    explicit FakeSender(std::chrono::microseconds service_time)
        : service_time_(service_time) {}

    SendOutcome send(const std::string&, long) override {
        std::this_thread::sleep_for(service_time_);
        calls_.fetch_add(1);

        SendOutcome out;
        out.success = true;
        out.http_code = 200;
        return out;
    }

    long long calls() const { return calls_.load(); }

private:
    std::chrono::microseconds service_time_;
    std::atomic<long long> calls_{0};
};