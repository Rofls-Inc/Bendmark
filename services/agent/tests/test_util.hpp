#pragma once

#include <cmath>
#include <cstdio>
#include <string>

namespace test {

inline int failures = 0;
inline int checks = 0;

inline void expect(bool cond, const std::string& msg) {
    ++checks;
    if (!cond) {
        ++failures;
        std::fprintf(stderr, "FAIL: %s\n", msg.c_str());
    }
}

inline void expect_eq_long(long long got, long long want, const std::string& msg) {
    ++checks;
    if (got != want) {
        ++failures;
        std::fprintf(stderr, "FAIL: %s (got %lld, want %lld)\n",
                     msg.c_str(), got, want);
    }
}

inline void expect_eq_double(double got, double want, double eps,
                             const std::string& msg) {
    ++checks;
    if (std::fabs(got - want) > eps) {
        ++failures;
        std::fprintf(stderr, "FAIL: %s (got %g, want %g)\n",
                     msg.c_str(), got, want);
    }
}

inline void expect_finite(double v, const std::string& msg) {
    ++checks;
    if (!std::isfinite(v)) {
        ++failures;
        std::fprintf(stderr, "FAIL: %s (got %g, not finite)\n", msg.c_str(), v);
    }
}

inline int finish(const char* name) {
    if (failures == 0) {
        std::fprintf(stderr, "%s: OK (%d checks)\n", name, checks);
        return 0;
    }
    std::fprintf(stderr, "%s: %d/%d FAILED\n", name, failures, checks);
    return 1;
}

} // namespace test