#include "stats.hpp"
#include "test_util.hpp"

#include <vector>

namespace {

std::vector<long> range_ms_as_us(long from_ms, long to_ms) {
    std::vector<long> v;
    for (long i = from_ms; i <= to_ms; ++i) v.push_back(i * 1000);
    return v;
}

void test_p50_p90_p99_for_1_to_100() {
    auto v = range_ms_as_us(1, 100);
    test::expect_eq_double(percentile_ms(v, 500), 50.0, 1e-9, "p50 of 1..100");
    test::expect_eq_double(percentile_ms(v, 900), 90.0, 1e-9, "p90 of 1..100");
    test::expect_eq_double(percentile_ms(v, 990), 99.0, 1e-9, "p99 of 1..100");
}

void test_p99_for_n50_is_max() {
    auto v = range_ms_as_us(1, 50);
    double p99 = percentile_ms(v, 990);
    double mx = v.back() / 1000.0;
    test::expect_eq_double(p99, mx, 1e-9, "p99 of 1..50 = max");
}

void test_n1_all_percentiles_equal() {
    std::vector<long> v{42 * 1000};
    test::expect_eq_double(percentile_ms(v, 500), 42.0, 1e-9, "p50 of [42]");
    test::expect_eq_double(percentile_ms(v, 900), 42.0, 1e-9, "p90 of [42]");
    test::expect_eq_double(percentile_ms(v, 990), 42.0, 1e-9, "p99 of [42]");
}

void test_empty_is_zero() {
    std::vector<long> v;
    test::expect_eq_double(percentile_ms(v, 500), 0.0, 1e-12, "p50 of empty");
    test::expect_eq_double(percentile_ms(v, 990), 0.0, 1e-12, "p99 of empty");
}

void test_edges() {
    std::vector<long> v{10 * 1000, 20 * 1000, 30 * 1000, 40 * 1000, 50 * 1000};
    test::expect_eq_double(percentile_ms(v, 0), 10.0, 1e-9, "p0 = min");
    test::expect_eq_double(percentile_ms(v, 1000), 50.0, 1e-9, "p1000 = max");
}

} // namespace

int main() {
    test_p50_p90_p99_for_1_to_100();
    test_p99_for_n50_is_max();
    test_n1_all_percentiles_equal();
    test_empty_is_zero();
    test_edges();
    return test::finish("test_percentiles");
}