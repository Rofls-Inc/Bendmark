#include "load_agent.hpp"
#include "fake_sender.hpp"
#include "sender.hpp"
#include "test_util.hpp"

#include <atomic>
#include <memory>

namespace {

void test_indexes_and_fractional_duration() {
    auto sender = std::make_shared<FakeSender>(std::chrono::microseconds(100));

    ScenarioConfig sc;
    sc.url = "http://fake";
    sc.timeout_ms = 5000;
    sc.max_concurrency = 1000;
    sc.steps = {
        {10, 0.2},
        {20, 0.5},
        {30, 0.3},
    };

    std::atomic<bool> stop{false};
    auto results = run_scenario(sc, sender, stop, nullptr);

    test::expect_eq_long(static_cast<long long>(results.size()), 3,
                         "three steps returned");

    if (results.size() != 3) return;

    for (size_t i = 0; i < results.size(); ++i) {
        test::expect_eq_long(results[i].index,
                             static_cast<long long>(i + 1),
                             "index sequential from 1");
    }

    test::expect_eq_double(results[0].duration_seconds, 0.2, 1e-12, "step 1 duration");
    test::expect_eq_double(results[1].duration_seconds, 0.5, 1e-12, "step 2 duration");
    test::expect_eq_double(results[2].duration_seconds, 0.3, 1e-12, "step 3 duration");
}

void test_skipped_step_is_still_emitted_and_scenario_continues() {
    auto sender = std::make_shared<FakeSender>(std::chrono::milliseconds(50));

    ScenarioConfig sc;
    sc.url = "http://fake";
    sc.timeout_ms = 5000;
    sc.max_concurrency = 1000;
    sc.concurrency_override = 1;
    sc.steps = {
        {100, 0.5},
        {10, 0.2},
    };

    std::atomic<bool> stop{false};
    auto results = run_scenario(sc, sender, stop, nullptr);

    test::expect_eq_long(static_cast<long long>(results.size()), 2,
                         "both steps emitted");

    if (results.size() != 2) return;

    test::expect(results[0].skipped_count > 0, "first step has skips");
    test::expect_eq_long(results[0].index, 1, "first step index 1");
    test::expect_eq_long(results[1].index, 2, "second step index 2");
}

void test_on_step_callback_called_for_each_step() {
    auto sender = std::make_shared<FakeSender>(std::chrono::microseconds(100));

    ScenarioConfig sc;
    sc.url = "http://fake";
    sc.timeout_ms = 5000;
    sc.max_concurrency = 1000;
    sc.steps = {
        {10, 0.1},
        {20, 0.1},
    };

    std::atomic<bool> stop{false};
    int calls = 0;
    int last_index = 0;

    auto results = run_scenario(sc, sender, stop, [&](const StepResult& s) {
        ++calls;
        last_index = s.index;
    });

    test::expect_eq_long(calls, 2, "callback called twice");
    test::expect_eq_long(last_index, 2, "last callback got index 2");
    test::expect_eq_long(static_cast<long long>(results.size()), 2, "two results");
}

} // namespace

int main() {
    test_indexes_and_fractional_duration();
    test_skipped_step_is_still_emitted_and_scenario_continues();
    test_on_step_callback_called_for_each_step();
    return test::finish("test_scenario");
}