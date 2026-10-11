#include "sender.hpp"

#include <curl/curl.h>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>

namespace {

size_t discard_body(char*, size_t size, size_t nmemb, void*) {
    return size * nmemb;
}

// Общий для всех рабочих потоков кеш соединений, DNS и TLS-сессий.
// Запрос из любого потока берёт свободное соединение из кеша, а после ответа
// возвращает его обратно. Открытых соединений получается примерно столько,
// сколько запросов одновременно в полёте, а не по одному на запрос.
class CurlShare {
public:
    CurlShare() : share_(curl_share_init()) {
        if (!share_) throw std::runtime_error("curl_share_init failed");
        curl_share_setopt(share_, CURLSHOPT_LOCKFUNC, &CurlShare::lock);
        curl_share_setopt(share_, CURLSHOPT_UNLOCKFUNC, &CurlShare::unlock);
        curl_share_setopt(share_, CURLSHOPT_USERDATA, this);
        curl_share_setopt(share_, CURLSHOPT_SHARE, CURL_LOCK_DATA_CONNECT);
        curl_share_setopt(share_, CURLSHOPT_SHARE, CURL_LOCK_DATA_DNS);
        curl_share_setopt(share_, CURLSHOPT_SHARE, CURL_LOCK_DATA_SSL_SESSION);
    }
    ~CurlShare() { curl_share_cleanup(share_); }

    CurlShare(const CurlShare&) = delete;
    CurlShare& operator=(const CurlShare&) = delete;

    CURLSH* get() const { return share_; }

private:
    static void lock(CURL*, curl_lock_data data, curl_lock_access, void* self) {
        static_cast<CurlShare*>(self)->mutexes_[data].lock();
    }
    static void unlock(CURL*, curl_lock_data data, void* self) {
        static_cast<CurlShare*>(self)->mutexes_[data].unlock();
    }

    CURLSH* share_;
    std::mutex mutexes_[CURL_LOCK_DATA_LAST];
};

constexpr long kMaxConnections = 1000;   // совпадает с --max-concurrency по умолчанию

class HttpSender : public Sender {
public:
    SendOutcome send(const std::string& url, long timeout_ms) override {
        SendOutcome out;

        CURL* curl = curl_easy_init();
        if (!curl) {
            out.error = "curl_easy_init failed";
            return out;
        }

        curl_easy_setopt(curl, CURLOPT_SHARE, share_.get());
        curl_easy_setopt(curl, CURLOPT_MAXCONNECTS, kMaxConnections);
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

private:
    CurlShare share_;
};

} // namespace

std::shared_ptr<Sender> make_http_sender() {
    return std::make_shared<HttpSender>();
}
