// Настоящий HttpSender против локального HTTP-сервера: метод, заголовки и тело
// из сценария должны дойти до сервера без изменений.

#include "http_test_server.hpp"
#include "sender.hpp"
#include "test_util.hpp"

#include <curl/curl.h>

#include <atomic>
#include <string>

namespace {

CapturedRequest send_one(HttpTestServer& server, const HttpRequest& request,
                         SendOutcome* outcome_out = nullptr) {
    auto sender = make_http_sender();
    std::atomic<bool> stop{false};
    auto outcome = sender->send(request, 5000, stop);
    if (outcome_out) *outcome_out = outcome;

    auto all = server.requests();
    test::expect(!all.empty(), request.method + ": request reached the server (" + outcome.error + ")");
    return all.empty() ? CapturedRequest{} : all.back();
}

void test_post_with_headers_and_body() {
    HttpTestServer server;
    HttpRequest request;
    request.url = server.url("/orders?side=buy&q=%20x");
    request.method = "POST";
    request.headers = {
        {"Content-Type", "application/json; charset=utf-8"},
        {"X-Trace-Id", "abc 123\t;=,\"quoted\""},
        {"Authorization", "Bearer token.with.dots"},
        {"X-Empty", ""},
    };
    request.body = "{\"ticker\": \"ABS\", \"qty\": 5, \"note\": \"привет\\n\"}\n";

    SendOutcome outcome;
    auto got = send_one(server, request, &outcome);

    test::expect(outcome.success, "POST succeeded");
    test::expect(got.method == "POST", "method is POST, got " + got.method);
    test::expect(got.target == "/orders?side=buy&q=%20x", "path and query unchanged: " + got.target);
    test::expect(got.body == request.body, "body unchanged: [" + got.body + "]");
    test::expect(got.values("Content-Type") == std::vector<std::string>{"application/json; charset=utf-8"},
                 "exactly one Content-Type, the scenario's");
    test::expect(got.header("X-Trace-Id") == std::optional<std::string>("abc 123\t;=,\"quoted\""),
                 "custom header value unchanged");
    test::expect(got.header("Authorization") == std::optional<std::string>("Bearer token.with.dots"),
                 "Authorization unchanged");
    test::expect(got.header("X-Empty") == std::optional<std::string>(""),
                 "header with empty value is sent, not removed");
    test::expect(got.header("Content-Length") == std::optional<std::string>(std::to_string(request.body.size())),
                 "Content-Length is the body size");
    test::expect(!got.header("Expect").has_value(), "no Expect: 100-continue");
}

void test_put_and_patch_send_body() {
    for (const std::string method : {"PUT", "PATCH"}) {
        HttpTestServer server;
        HttpRequest request;
        request.url = server.url("/item/1");
        request.method = method;
        request.headers = {{"Content-Type", "text/plain"}};
        request.body = "value=" + method;

        auto got = send_one(server, request);
        test::expect(got.method == method, method + ": method unchanged, got " + got.method);
        test::expect(got.body == request.body, method + ": body unchanged");
    }
}

void test_body_without_content_type_gets_none() {
    HttpTestServer server;
    HttpRequest request;
    request.url = server.url("/raw");
    request.method = "POST";
    request.body = "a=1&b=2";

    auto got = send_one(server, request);
    test::expect(got.body == "a=1&b=2", "body unchanged");
    test::expect(!got.header("Content-Type").has_value(),
                 "libcurl's default form Content-Type is not added");
}

void test_post_without_body_has_zero_length() {
    HttpTestServer server;
    HttpRequest request;
    request.url = server.url("/negotiate");
    request.method = "POST";

    auto got = send_one(server, request);
    test::expect(got.method == "POST", "empty POST is still POST");
    test::expect(got.header("Content-Length") == std::optional<std::string>("0"), "Content-Length: 0");
    test::expect(got.body.empty(), "no body");
}

void test_large_body_is_sent_whole() {
    HttpTestServer server;
    HttpRequest request;
    request.url = server.url("/bulk");
    request.method = "POST";
    request.headers = {{"Content-Type", "application/octet-stream"}};
    request.body.reserve(kMaxBodyBytes);
    for (size_t i = 0; i < kMaxBodyBytes; ++i) request.body.push_back(static_cast<char>('a' + i % 26));

    auto got = send_one(server, request);
    test::expect(got.body == request.body, "64 KiB body arrives whole");
    test::expect(!got.header("Expect").has_value(), "no Expect for a large body");
}

void test_get_and_delete_have_no_body() {
    for (const std::string method : {"GET", "DELETE"}) {
        HttpTestServer server;
        HttpRequest request;
        request.url = server.url("/thing");
        request.method = method;
        request.headers = {{"Accept", "application/json"}};

        auto got = send_one(server, request);
        test::expect(got.method == method, method + ": method unchanged, got " + got.method);
        test::expect(got.body.empty(), method + ": no body");
        test::expect(!got.header("Content-Type").has_value(), method + ": no Content-Type");
        test::expect(got.values("Accept") == std::vector<std::string>{"application/json"},
                     method + ": Accept from the scenario replaces libcurl's default");
    }
}

void test_redirects_followed_only_for_get() {
    {
        HttpTestServer server;
        HttpRequest request;
        request.url = server.url("/redirect");
        request.method = "POST";
        request.headers = {{"Content-Type", "text/plain"}};
        request.body = "x";

        SendOutcome outcome;
        send_one(server, request, &outcome);
        test::expect_eq_long(static_cast<long long>(server.requests().size()), 1, "POST redirect not followed");
        test::expect_eq_long(outcome.http_code, 302, "POST outcome is the 302 itself");
        test::expect(outcome.success, "3xx counts as success");
    }
    {
        HttpTestServer server;
        HttpRequest request;
        request.url = server.url("/redirect");

        SendOutcome outcome;
        auto got = send_one(server, request, &outcome);
        test::expect_eq_long(static_cast<long long>(server.requests().size()), 2, "GET redirect followed");
        test::expect(got.target == "/target", "GET ends at Location");
        test::expect_eq_long(outcome.http_code, 200, "GET outcome is the final 200");
    }
}

HttpRequest valid() {
    HttpRequest r;
    r.url = "http://abstock:8080/";
    r.method = "POST";
    r.headers = {{"Content-Type", "application/json"}};
    r.body = "{}";
    return r;
}

void expect_invalid(HttpRequest r, const std::string& what) {
    auto error = validate_request(r);
    test::expect(!error.empty(), "rejected: " + what);
}

void test_validation() {
    test::expect(validate_request(valid()).empty(), "valid POST accepted: " + validate_request(valid()));
    for (const std::string method : {"GET", "POST", "PUT", "PATCH", "DELETE"}) {
        HttpRequest r = valid();
        r.method = method;
        r.body.clear();
        test::expect(validate_request(r).empty(), method + " without body accepted");
    }

    for (const std::string method : {"", "get", "Post", "HEAD", "OPTIONS", "FETCH", " GET"}) {
        HttpRequest r = valid();
        r.method = method;
        expect_invalid(r, "method '" + method + "'");
    }
    for (const std::string method : {"GET", "DELETE"}) {
        HttpRequest r = valid();
        r.method = method;
        expect_invalid(r, "body with " + method);
    }
    {
        HttpRequest r = valid();
        r.url = "ftp://x/";
        expect_invalid(r, "non-http url");
    }
    {
        HttpRequest r = valid();
        r.body.assign(kMaxBodyBytes + 1, 'x');
        expect_invalid(r, "body over 64 KiB");
        r.body.assign(kMaxBodyBytes, 'x');
        test::expect(validate_request(r).empty(), "body of exactly 64 KiB accepted");
    }
    for (const std::string name : {"", "Bad Name", "X:Y", "Привет", "X\r\nInjected"}) {
        HttpRequest r = valid();
        r.headers.emplace_back(name, "v");
        expect_invalid(r, "header name '" + name + "'");
    }
    for (const std::string& value : {std::string("a\r\nX-Injected: 1"), std::string("a\nb"), std::string("a\0b", 3),
                                    std::string("a\x7f")}) {
        HttpRequest r = valid();
        r.headers.emplace_back("X-Value", value);
        expect_invalid(r, "control characters in header value");
    }
    for (const std::string name : {"Content-Length", "transfer-encoding"}) {
        HttpRequest r = valid();
        r.headers.emplace_back(name, "5");
        expect_invalid(r, name + " set by the scenario");
    }
    {
        HttpRequest r = valid();
        r.headers.emplace_back("content-type", "text/plain");
        expect_invalid(r, "duplicate header differing in case");
    }
    {
        HttpRequest r = valid();
        r.headers.clear();
        for (size_t i = 0; i < kMaxHeaders + 1; ++i) r.headers.emplace_back("X-H" + std::to_string(i), "v");
        expect_invalid(r, "too many headers");
    }
    {
        HttpRequest r = valid();
        r.headers.emplace_back("X-Big", std::string(kMaxHeaderBytes, 'v'));
        expect_invalid(r, "headers over 8 KiB");
    }
}

} // namespace

int main() {
    curl_global_init(CURL_GLOBAL_DEFAULT);
    test_validation();
    test_post_with_headers_and_body();
    test_put_and_patch_send_body();
    test_body_without_content_type_gets_none();
    test_post_without_body_has_zero_length();
    test_large_body_is_sent_whole();
    test_get_and_delete_have_no_body();
    test_redirects_followed_only_for_get();
    curl_global_cleanup();
    return test::finish("test_http_request");
}
