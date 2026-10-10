#include "sender.hpp"

#include <curl/curl.h>

namespace {

size_t discard_body(char*, size_t size, size_t nmemb, void*) {
    return size * nmemb;
}

class HttpSender : public Sender {
public:
    RequestResult send(const std::string& url,
                       long timeout_ms,
                       std::chrono::steady_clock::time_point scheduled_at) override {
        RequestResult r;

        CURL* curl = curl_easy_init();
        if (!curl) {
            r.error = "curl_easy_init failed";
            return r;
        }

        curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
        curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, discard_body);
        curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS, timeout_ms);
        curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT_MS, timeout_ms);
        curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L);
        curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);

        auto start = std::chrono::steady_clock::now();
        CURLcode rc = curl_easy_perform(curl);
        auto end = std::chrono::steady_clock::now();

        r.latency_us = std::chrono::duration_cast<std::chrono::microseconds>(
            end - scheduled_at).count();
        r.send_lag_us = std::chrono::duration_cast<std::chrono::microseconds>(
            start - scheduled_at).count();

        if (rc == CURLE_OK) {
            long code = 0;
            curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &code);
            r.http_code = code;

            if (code >= 200 && code < 400) {
                r.success = true;
            } else {
                r.error = "HTTP " + std::to_string(code);
            }
        } else {
            r.error = curl_easy_strerror(rc);
        }

        curl_easy_cleanup(curl);
        return r;
    }
};

} // namespace

std::shared_ptr<Sender> make_http_sender() {
    return std::make_shared<HttpSender>();
}