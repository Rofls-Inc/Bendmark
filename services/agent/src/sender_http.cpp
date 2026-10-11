#include "sender.hpp"

#include <curl/curl.h>
#include <string>

namespace {

size_t discard_body(char*, size_t size, size_t nmemb, void*) {
    return size * nmemb;
}

class HttpSender : public Sender {
public:
    SendOutcome send(const std::string& url, long timeout_ms) override {
        SendOutcome out;

        CURL* curl = curl_easy_init();
        if (!curl) {
            out.error = "curl_easy_init failed";
            return out;
        }

        curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
        curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, discard_body);
        curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS, timeout_ms);
        curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT_MS, timeout_ms);
        curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L);
        curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);

        CURLcode rc = curl_easy_perform(curl);

        if (rc == CURLE_OK) {
            long code = 0;
            curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &code);
            out.http_code = code;

            if (code >= 200 && code < 400) {
                out.success = true;
            } else {
                out.error = "HTTP " + std::to_string(code);
            }
        } else {
            out.error = curl_easy_strerror(rc);
        }

        curl_easy_cleanup(curl);
        return out;
    }
};

} // namespace

std::shared_ptr<Sender> make_http_sender() {
    return std::make_shared<HttpSender>();
}