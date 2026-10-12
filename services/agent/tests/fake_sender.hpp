#pragma once

#include "sender.hpp"

#include <atomic>
#include <chrono>
#include <thread>

class FakeSender : public Sender {
public:
    explicit FakeSender(std::chrono::microseconds service_time)
        : service_time_(service_time) {}

    SendOutcome send(const std::string&,
                     long,
                     std::atomic<bool>& stop) override {
        // Ждём сервисное время, но не дольше, чем потребуется для проверки
        // флага остановки. Если stop выставлен — прерываем, как это сделал бы
        // настоящий HttpSender через CURLOPT_XFERINFOFUNCTION.
        const auto step = std::chrono::milliseconds(1);
        auto remaining = service_time_;

        while (remaining > std::chrono::microseconds(0)) {
            if (stop.load()) {
                SendOutcome out;
                out.error = "aborted";
                return out;
            }

            auto nap = remaining < step ? remaining : step;
            std::this_thread::sleep_for(nap);
            remaining -= nap;
        }

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