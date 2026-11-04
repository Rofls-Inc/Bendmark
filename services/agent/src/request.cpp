#include "sender.hpp"

#include <algorithm>
#include <cctype>
#include <set>
#include <string>

namespace {

bool is_http_url(const std::string& s) {
    return s.rfind("http://", 0) == 0 || s.rfind("https://", 0) == 0;
}

// tchar из RFC 9110: имя заголовка - непустой токен
bool is_token(const std::string& s) {
    if (s.empty()) return false;
    static const std::string extra = "!#$%&'*+-.^_`|~";
    return std::all_of(s.begin(), s.end(), [](unsigned char c) {
        return std::isalnum(c) || extra.find(static_cast<char>(c)) != std::string::npos;
    });
}

// Значение без управляющих символов, кроме табуляции: CR и LF позволили бы
// дописать в запрос чужие заголовки
bool is_header_value(const std::string& s) {
    return std::none_of(s.begin(), s.end(), [](unsigned char c) {
        return (c < 0x20 && c != '\t') || c == 0x7f;
    });
}

std::string lower(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

} // namespace

std::string validate_request(const HttpRequest& request) {
    if (!is_http_url(request.url)) {
        return "target.url must be http/https";
    }

    const auto& m = request.method;
    if (m != "GET" && m != "POST" && m != "PUT" && m != "PATCH" && m != "DELETE") {
        return "target.method must be GET, POST, PUT, PATCH or DELETE";
    }

    if (!request.body.empty() && (m == "GET" || m == "DELETE")) {
        return "target.body is not allowed with " + m;
    }
    if (request.body.size() > kMaxBodyBytes) {
        return "target.body exceeds " + std::to_string(kMaxBodyBytes) + " bytes";
    }

    if (request.headers.size() > kMaxHeaders) {
        return "target.headers: more than " + std::to_string(kMaxHeaders) + " headers";
    }
    std::size_t total = 0;
    std::set<std::string> seen;
    for (const auto& [name, value] : request.headers) {
        if (!is_token(name)) {
            return "target.headers: invalid header name '" + name + "'";
        }
        const auto key = lower(name);
        if (key == "content-length" || key == "transfer-encoding") {
            return "target.headers: " + name + " is set by the agent";
        }
        if (!seen.insert(key).second) {
            return "target.headers: duplicate header " + name;
        }
        if (!is_header_value(value)) {
            return "target.headers: control characters in value of " + name;
        }
        total += name.size() + value.size();
    }
    if (total > kMaxHeaderBytes) {
        return "target.headers exceed " + std::to_string(kMaxHeaderBytes) + " bytes";
    }

    return "";
}
