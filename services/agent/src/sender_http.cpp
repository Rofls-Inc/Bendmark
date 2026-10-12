#include "sender.hpp"

#include <curl/curl.h>
#include <mutex>
#include <string>

namespace {

size_t discard_body(char*, size_t size, size_t nmemb, void*) {
    return size * nmemb;
}

int progress_cb(void* p, curl_off_t, curl_off_t, curl_off_t, curl_off_t) {
    return static_cast<std::atomic<bool>*>(p)->load() ? 1 : 0;
}

std::once_flag g_share_once;
CURLSH* g_share = nullptr;
std::mutex g_share_mtx;

void share_lock_cb(CURL*, curl_lock_data, curl_lock_access, void*) {
    g_share_mtx.lock();
}

void share_unlock_cb(CURL*, curl_lock_data, void*) {
    g_share_mtx.unlock();
}

void ensure_share() {
    std::call_once(g_share_once, [] {
        g_share = curl_share_init();
        curl_share_setopt(g_share, CURLSHOPT_LOCKFUNC, share_lock_cb);
        curl_share_setopt(g_share, CURLSHOPT_UNLOCKFUNC, share_unlock_cb);
        curl_share_setopt(g_share, CURLSHOPT_SHARE, CURL_LOCK_DATA_CONNECT);
        curl_share_setopt(g_share, CURLSHOPT_SHARE, CURL_LOCK_DATA_DNS);
    });
}

class HttpSender : public Sender {
public:
    SendOutcome send(const std::string& url,
                     long timeout_ms,
                     std::atomic<bool>& stop) override {
        SendOutcome out;

        ensure_share();

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
        curl_easy_setopt(curl, CURLOPT_SHARE, g_share);
        curl_easy_setopt(curl, CURLOPT_MAXCONNECTS, 16L);
        curl_easy_setopt(curl, CURLOPT_XFERINFOFUNCTION, progress_cb);
        curl_easy_setopt(curl, CURLOPT_XFERINFODATA, &stop);
        curl_easy_setopt(curl, CURLOPT_NOPROGRESS, 0L);

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
        } else if (rc == CURLE_ABORTED_BY_CALLBACK) {
            out.error = "aborted";
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