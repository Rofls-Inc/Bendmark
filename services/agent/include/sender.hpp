#pragma once

#include <atomic>
#include <cstddef>
#include <memory>
#include <string>
#include <utility>
#include <vector>

// Один и тот же запрос, который агент отправляет на каждом тике (Target из контракта)
struct HttpRequest {
    std::string url;
    std::string method = "GET";
    std::vector<std::pair<std::string, std::string>> headers;
    // Пустое тело - запрос без тела
    std::string body;
};

// Пределы совпадают с проверкой координатора (ScenarioValidator) и proto/README.md
constexpr std::size_t kMaxHeaders = 32;
constexpr std::size_t kMaxHeaderBytes = 8 * 1024;   // имена и значения вместе
constexpr std::size_t kMaxBodyBytes = 64 * 1024;

// Пустая строка - запрос корректен; иначе текст ошибки для INVALID_ARGUMENT
std::string validate_request(const HttpRequest& request);

struct SendOutcome {
    bool success = false;
    long http_code = 0;
    std::string error;
};

class Sender {
public:
    virtual ~Sender() = default;

    virtual SendOutcome send(const HttpRequest& request,
                             long timeout_ms,
                             std::atomic<bool>& stop) = 0;
};

std::shared_ptr<Sender> make_http_sender();
