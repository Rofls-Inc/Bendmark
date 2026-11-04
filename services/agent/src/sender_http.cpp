#include "sender.hpp"

#include <curl/curl.h>
#include <algorithm>
#include <atomic>
#include <cctype>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>

namespace {

size_t discard_body(char*, size_t size, size_t nmemb, void*) {
    return size * nmemb;
}

// Прерывает запрос, когда выставлен флаг остановки (Stop, отмена вызова, deadline).
int progress_cb(void* p, curl_off_t, curl_off_t, curl_off_t, curl_off_t) {
    return static_cast<std::atomic<bool>*>(p)->load() ? 1 : 0;
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

bool has_header(const HttpRequest& request, const std::string& lower_name) {
    return std::any_of(request.headers.begin(), request.headers.end(), [&](const auto& h) {
        const auto& name = h.first;
        return name.size() == lower_name.size()
            && std::equal(name.begin(), name.end(), lower_name.begin(), [](char a, char b) {
                   return std::tolower(static_cast<unsigned char>(a)) == b;
               });
    });
}

// Заголовки запроса в формате libcurl. Пустой список - nullptr
curl_slist* build_headers(const HttpRequest& request, bool sends_body) {
    curl_slist* list = nullptr;
    auto append = [&](const std::string& line) {
        curl_slist* next = curl_slist_append(list, line.c_str());
        if (!next) {
            curl_slist_free_all(list);
            throw std::runtime_error("curl_slist_append failed");
        }
        list = next;
    };

    for (const auto& [name, value] : request.headers) {
        // "Name:" libcurl понимает как «убрать заголовок», пустое значение пишется через ';'
        append(value.empty() ? name + ";" : name + ": " + value);
    }
    if (sends_body) {
        // Иначе libcurl сам добавит application/x-www-form-urlencoded и
        // Expect: 100-continue для больших тел: запрос дошёл бы не таким, как в сценарии
        if (!has_header(request, "content-type")) append("Content-Type:");
        if (!has_header(request, "expect")) append("Expect:");
    }
    return list;
}

class HttpSender : public Sender {
public:
    SendOutcome send(const HttpRequest& request,
                     long timeout_ms,
                     std::atomic<bool>& stop) override {
        SendOutcome out;

        CURL* curl = curl_easy_init();
        if (!curl) {
            out.error = "curl_easy_init failed";
            return out;
        }

        const bool is_get = request.method == "GET";
        // DELETE и GET без тела; POST, PUT и PATCH отправляют тело, даже пустое (Content-Length: 0)
        const bool sends_body = !is_get && request.method != "DELETE";

        curl_slist* headers = nullptr;
        try {
            headers = build_headers(request, sends_body);
        } catch (const std::exception& e) {
            curl_easy_cleanup(curl);
            out.error = e.what();
            return out;
        }

        curl_easy_setopt(curl, CURLOPT_SHARE, share_.get());
        curl_easy_setopt(curl, CURLOPT_MAXCONNECTS, kMaxConnections);
        curl_easy_setopt(curl, CURLOPT_URL, request.url.c_str());
        curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, discard_body);
        curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS, timeout_ms);
        curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT_MS, timeout_ms);
        if (sends_body) {
            // Тело не копируется: request живёт до конца send
            curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE_LARGE,
                             static_cast<curl_off_t>(request.body.size()));
            curl_easy_setopt(curl, CURLOPT_POSTFIELDS, request.body.data());
        }
        // POSTFIELDS сам делает запрос POST; PUT, PATCH и DELETE задаются строкой метода
        if (!is_get && request.method != "POST") {
            curl_easy_setopt(curl, CURLOPT_CUSTOMREQUEST, request.method.c_str());
        }
        if (headers) {
            curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);
        }
        // Редиректы проходим только для GET. Для остальных методов замеряем сам запрос:
        // ответ 3xx считается успехом, как и раньше, а повтор POST по Location исказил бы замер
        curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, is_get ? 1L : 0L);
        curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);
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
        curl_slist_free_all(headers);
        return out;
    }

private:
    CurlShare share_;
};

} // namespace

std::shared_ptr<Sender> make_http_sender() {
    return std::make_shared<HttpSender>();
}
