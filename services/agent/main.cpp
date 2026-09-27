#include <curl/curl.h>
#include <chrono>
#include <string>
#include <vector>
#include <thread>
#include <atomic>
#include <cstdio>
#include <cstdlib>

std::vector<double> latencies;
std::atomic<bool> stop{false};

size_t discard(void*, size_t size, size_t nmemb, void*) {
    return size * nmemb;
}

void worker(const std::string& url, int rate_per_sec) {
    const auto interval = std::chrono::microseconds(1000000 / rate_per_sec);
    CURL* curl = curl_easy_init();
    if (!curl) {
        std::fprintf(stderr, "curl_easy_init failed\n");
        return;
    }
    curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, discard);
    curl_easy_setopt(curl, CURLOPT_TIMEOUT, 10L);

    while (!stop) {
        auto start = std::chrono::steady_clock::now();
        CURLcode res = curl_easy_perform(curl);
        auto end = std::chrono::steady_clock::now();

        if (res == CURLE_OK) {
            latencies.push_back(
                std::chrono::duration<double, std::milli>(end - start).count());
        }
        std::this_thread::sleep_for(interval);
    }
    curl_easy_cleanup(curl);
}

int main(int argc, char** argv) {
    if (argc != 4) {
        std::fprintf(stderr,
            "Usage: %s <url> <requests_per_sec> <duration_sec>\n", argv[0]);
        return 1;
    }

    std::string url = argv[1];
    int rate = std::atoi(argv[2]);
    int duration = std::atoi(argv[3]);

    if (rate <= 0 || duration <= 0) {
        std::fprintf(stderr, "rate and duration must be positive\n");
        return 1;
    }

    curl_global_init(CURL_GLOBAL_DEFAULT);

    std::thread t(worker, url, rate);
    std::this_thread::sleep_for(std::chrono::seconds(duration));
    stop = true;
    t.join();

    curl_global_cleanup();

    if (latencies.empty()) {
        std::printf("No successful requests.\n");
        return 0;
    }

    double sum = 0;
    for (double x : latencies) sum += x;
    std::printf("Requests: %zu, Avg latency: %.2f ms\n",
                latencies.size(), sum / latencies.size());
    return 0;
}