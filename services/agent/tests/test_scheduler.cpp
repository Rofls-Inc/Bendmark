#include "load_agent.hpp"
#include "fake_sender.hpp"
#include "sender.hpp"
#include "test_util.hpp"

#include <atomic>
#include <cmath>
#include <cstdlib>
#include <memory>

namespace {

StepConfig step(double rps, double seconds) {
    StepConfig s;
    s.target_rps = rps;
    s.duration_seconds = seconds;
    return s;
}

ScenarioConfig opt(long timeout_ms, int max_conc) {
    ScenarioConfig o;
    o.url = "http://fake";
    o.timeout_ms = timeout_ms;
    o.max_concurrency = max_conc;
    return o;
}

void test_no_skips_with_enough_workers() {
    auto sender = std::make_shared<FakeSender>(std::chrono::microseconds(100));
    auto o = opt(5000, 1000);
    std::atomic<bool> stop{false};

    auto r = run_step("http://fake", step(100, 1.0), 1, o, sender, stop);
    test::expect(r.has_value(), "step returned a result");
    if (!r) return;

    auto s = *r;
    test::expect_eq_long(s.skipped_count, 0, "no skips with enough workers");

    long long expected = 100;
    long long delta = std::llabs(s.request_count - expected);
    test::expect(delta <= 1, "request_count close to rps * duration");
}

void test_skips_with_slow_sender_and_one_worker() {
    auto sender = std::make_shared<FakeSender>(std::chrono::milliseconds(50));
    auto o = opt(5000, 1000);
    o.concurrency_override = 1;
    std::atomic<bool> stop{false};

    auto r = run_step("http://fake", step(100, 1.0), 1, o, sender, stop);
    test::expect(r.has_value(), "step returned a result");
    if (!r) return;

    auto s = *r;
    test::expect(s.skipped_count > 0, "skips appear with slow sender and 1 worker");

    long long ticks = 100;
    long long total = s.request_count + s.skipped_count;
    long long delta = std::llabs(total - ticks);
    test::expect(delta <= 1, "request_count + skipped_count = number of ticks");
}

void test_latency_from_scheduled_tick() {
    auto sender = std::make_shared<FakeSender>(std::chrono::milliseconds(50));
    auto o = opt(5000, 1000);
    o.concurrency_override = 1;
    std::atomic<bool> stop{false};

    auto r = run_step("http://fake", step(100, 1.0), 1, o, sender, stop);
    test::expect(r.has_value(), "step returned a result");
    if (!r) return;

    auto s = *r;
    test::expect(s.latency_p50_ms > 50.0,
                 "latency includes queue wait (p50 > service time)");
}

void test_empty_run_result_produces_zeros() {
    RunResult empty;
    auto s = compute_step_result(1, step(100, 1.0), empty);

    test::expect_eq_long(s.request_count, 0, "request_count = 0");
    test::expect_eq_long(s.skipped_count, 0, "skipped_count = 0");
    test::expect_eq_long(s.error_count, 0, "error_count = 0");
    test::expect_eq_double(s.throughput_rps, 0.0, 1e-12, "throughput = 0");
    test::expect_eq_double(s.error_rate_percent, 0.0, 1e-12, "error rate = 0");
    test::expect_finite(s.latency_p50_ms, "p50 finite");
    test::expect_finite(s.latency_p90_ms, "p90 finite");
    test::expect_finite(s.latency_p99_ms, "p99 finite");
    test::expect_eq_double(s.latency_p50_ms, 0.0, 1e-12, "p50 = 0");
    test::expect_eq_double(s.latency_p90_ms, 0.0, 1e-12, "p90 = 0");
    test::expect_eq_double(s.latency_p99_ms, 0.0, 1e-12, "p99 = 0");
}

} // namespace

int main() {
    test_no_skips_with_enough_workers();
    test_skips_with_slow_sender_and_one_worker();
    test_latency_from_scheduled_tick();
    test_empty_run_result_produces_zeros();
    return test::finish("test_scheduler");
}