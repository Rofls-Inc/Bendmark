#pragma once

// Минимальный HTTP/1.1-сервер для тестов отправителя: принимает соединения на
// 127.0.0.1, запоминает каждый запрос как пришёл (метод, путь, заголовки, тело)
// и отвечает 200. Путь /redirect отвечает 302 на /target.
// Ответы с Connection: close: на каждый запрос новое соединение, разбирать
// конвейер запросов не нужно.

#include <arpa/inet.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdlib>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

struct CapturedRequest {
    std::string method;
    std::string target;
    // Как пришли: имя и значение без пробелов вокруг двоеточия, в исходном порядке
    std::vector<std::pair<std::string, std::string>> headers;
    std::string body;

    std::vector<std::string> values(const std::string& name) const {
        std::vector<std::string> out;
        for (const auto& [n, v] : headers) {
            if (equal_ci(n, name)) out.push_back(v);
        }
        return out;
    }

    std::optional<std::string> header(const std::string& name) const {
        auto all = values(name);
        if (all.empty()) return std::nullopt;
        return all.front();
    }

    static bool equal_ci(const std::string& a, const std::string& b) {
        return a.size() == b.size()
            && std::equal(a.begin(), a.end(), b.begin(), [](char x, char y) {
                   return std::tolower(static_cast<unsigned char>(x))
                       == std::tolower(static_cast<unsigned char>(y));
               });
    }
};

class HttpTestServer {
public:
    HttpTestServer() {
        fd_ = ::socket(AF_INET, SOCK_STREAM, 0);
        if (fd_ < 0) throw std::runtime_error("socket failed");
        int one = 1;
        ::setsockopt(fd_, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));

        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        addr.sin_port = 0;
        if (::bind(fd_, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0
            || ::listen(fd_, 128) != 0) {
            ::close(fd_);
            throw std::runtime_error("bind/listen failed");
        }
        socklen_t len = sizeof(addr);
        ::getsockname(fd_, reinterpret_cast<sockaddr*>(&addr), &len);
        port_ = ntohs(addr.sin_port);

        thread_ = std::thread([this] { serve(); });
    }

    ~HttpTestServer() {
        stopping_.store(true);
        thread_.join();
        ::close(fd_);
    }

    HttpTestServer(const HttpTestServer&) = delete;
    HttpTestServer& operator=(const HttpTestServer&) = delete;

    std::string url(const std::string& path) const {
        return "http://127.0.0.1:" + std::to_string(port_) + path;
    }

    std::vector<CapturedRequest> requests() const {
        std::lock_guard<std::mutex> lk(mu_);
        return requests_;
    }

private:
    void serve() {
        while (!stopping_.load()) {
            pollfd p{fd_, POLLIN, 0};
            if (::poll(&p, 1, 20) <= 0) continue;
            int client = ::accept(fd_, nullptr, nullptr);
            if (client < 0) continue;
            handle(client);
            ::close(client);
        }
    }

    // Читает ровно один запрос: заголовки до пустой строки, затем Content-Length байт тела
    void handle(int client) {
        std::string data;
        char buf[4096];
        size_t header_end = std::string::npos;
        while ((header_end = data.find("\r\n\r\n")) == std::string::npos) {
            if (!read_some(client, buf, sizeof(buf), data)) return;
        }

        CapturedRequest req;
        std::string head = data.substr(0, header_end);
        size_t line_end = head.find("\r\n");
        std::string request_line = head.substr(0, line_end);
        size_t sp1 = request_line.find(' ');
        size_t sp2 = request_line.find(' ', sp1 + 1);
        req.method = request_line.substr(0, sp1);
        req.target = request_line.substr(sp1 + 1, sp2 - sp1 - 1);

        size_t pos = line_end == std::string::npos ? head.size() : line_end + 2;
        while (pos < head.size()) {
            size_t next = head.find("\r\n", pos);
            if (next == std::string::npos) next = head.size();
            std::string line = head.substr(pos, next - pos);
            size_t colon = line.find(':');
            std::string name = line.substr(0, colon);
            size_t value_start = line.find_first_not_of(" \t", colon + 1);
            std::string value = value_start == std::string::npos ? "" : line.substr(value_start);
            while (!value.empty() && (value.back() == ' ' || value.back() == '\t')) value.pop_back();
            req.headers.emplace_back(name, value);
            pos = next + 2;
        }

        size_t length = 0;
        if (auto cl = req.header("Content-Length")) length = std::stoul(*cl);
        std::string rest = data.substr(header_end + 4);
        while (rest.size() < length) {
            if (!read_some(client, buf, sizeof(buf), rest)) return;
        }
        req.body = rest.substr(0, length);

        std::string response = req.target == "/redirect"
            ? "HTTP/1.1 302 Found\r\nLocation: /target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
        ::send(client, response.data(), response.size(), MSG_NOSIGNAL);

        std::lock_guard<std::mutex> lk(mu_);
        requests_.push_back(std::move(req));
    }

    static bool read_some(int client, char* buf, size_t size, std::string& out) {
        pollfd p{client, POLLIN, 0};
        if (::poll(&p, 1, 2000) <= 0) return false;
        ssize_t n = ::recv(client, buf, size, 0);
        if (n <= 0) return false;
        out.append(buf, static_cast<size_t>(n));
        return true;
    }

    int fd_ = -1;
    int port_ = 0;
    std::atomic<bool> stopping_{false};
    std::thread thread_;
    mutable std::mutex mu_;
    std::vector<CapturedRequest> requests_;
};
