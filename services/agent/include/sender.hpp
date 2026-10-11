#pragma once

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

    // send() вызывается параллельно из нескольких воркеров.
    // Реализация обязана быть потокобезопасной: без общего изменяемого состояния
    // либо с собственной синхронизацией.
    virtual SendOutcome send(const std::string& target, long timeout_ms) = 0;
};

std::shared_ptr<Sender> make_http_sender();