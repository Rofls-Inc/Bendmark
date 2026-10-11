#include "load_agent.hpp"
#include "test_util.hpp"

namespace {

StepConfig step(double rps) {
    StepConfig s;
    s.target_rps = rps;
    s.duration_seconds = 1.0;
    return s;
}

ScenarioConfig opt(long timeout_ms, int max_conc, int override = 0) {
    ScenarioConfig o;
    o.timeout_ms = timeout_ms;
    o.max_concurrency = max_conc;
    o.concurrency_override = override;
    return o;
}

void test_formula() {
    test::expect_eq_long(compute_workers(step(100), opt(5000, 1000)), 501,
                         "100 rps * 5 s + 1 = 501");
    test::expect_eq_long(compute_workers(step(10), opt(1000, 1000)), 11,
                         "10 rps * 1 s + 1 = 11");
    test::expect_eq_long(compute_workers(step(1), opt(100, 1000)), 2,
                         "1 rps * 0.1 s + 1 = 2");
}

void test_cap_at_max_concurrency() {
    test::expect_eq_long(compute_workers(step(1000), opt(5000, 100)), 100,
                         "capped at max_concurrency");
    test::expect_eq_long(compute_workers(step(10000), opt(5000, 1000)), 1000,
                         "capped at max_concurrency (big)");
}

void test_override() {
    test::expect_eq_long(compute_workers(step(100), opt(5000, 1000, 7)), 7,
                         "override wins over formula");
    test::expect_eq_long(compute_workers(step(100), opt(5000, 1000, 1)), 1,
                         "override = 1");
}

} // namespace

int main() {
    test_formula();
    test_cap_at_max_concurrency();
    test_override();
    return test::finish("test_workers");
}