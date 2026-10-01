#include "load_agent.hpp"

#include <curl/curl.h>
#include <condition_variable>
#include <mutex>
#include <thread>
#include <utility>

namespace {

size_t discard_body(char*, size_t size, size_t nmemb, void*) {
    return size * nmemb;
}

RequestResult do_request(const std::string& url, long timeout_ms) {
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

    r.latency_us = std::chrono::duration_cast<std::chrono::microseconds>(end - start).count();

    if (rc == CURLE_OK) {
        r.success = true;
    } else {
        r.error = curl_easy_strerror(rc);
    }

    curl_easy_cleanup(curl);
    return r;
}

} // namespace

LoadGenerator::LoadGenerator(LoadConfig cfg) : cfg_(std::move(cfg)) {}

RunResult LoadGenerator::run() {
    curl_global_init(CURL_GLOBAL_DEFAULT);

    RunResult out;
    std::mutex results_mtx;
    std::mutex work_mtx;
    std::condition_variable work_cv;
    std::vector<std::thread> workers;
    int outstanding = 0;
    int pending = 0;
    bool stopping = false;

    for (int i = 0; i < cfg_.concurrency; ++i) {
        workers.emplace_back([&, url = cfg_.url, timeout = cfg_.timeout_ms] {
            while (true) {
                {
                    std::unique_lock<std::mutex> lk(work_mtx);
                    work_cv.wait(lk, [&] { return pending > 0 || stopping; });
                    if (pending == 0) return;
                    --pending;
                }

                RequestResult r = do_request(url, timeout);
                {
                    std::lock_guard<std::mutex> lk(results_mtx);
                    out.requests.push_back(std::move(r));
                }
                {
                    std::lock_guard<std::mutex> lk(work_mtx);
                    --outstanding;
                }
            }
        });
    }

    const auto interval = std::chrono::duration<double>(1.0 / cfg_.rate_per_sec);
    const auto start_time = std::chrono::steady_clock::now();
    const auto end_time = start_time + cfg_.duration;
    auto next_tick = start_time;

    while (next_tick < end_time) {
        std::this_thread::sleep_until(next_tick);
        next_tick += std::chrono::duration_cast<std::chrono::steady_clock::duration>(interval);

        bool queued = false;
        {
            std::lock_guard<std::mutex> lk(work_mtx);
            if (outstanding >= cfg_.concurrency) {
                ++out.skipped;
            } else {
                ++outstanding;
                ++pending;
                queued = true;
            }
        }
        if (queued) work_cv.notify_one();
    }

    {
        std::lock_guard<std::mutex> lk(work_mtx);
        stopping = true;
    }
    work_cv.notify_all();
    for (auto& worker : workers) worker.join();

    curl_global_cleanup();

    return out;
}
