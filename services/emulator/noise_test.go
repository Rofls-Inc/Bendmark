package main

import (
	"testing"
	"time"
)

func TestSeedRepeatsJitterAndOutlierDecisions(t *testing.T) {
	cfg := noiseConfig{
		jitter: 20 * time.Millisecond, outlierRate: 0.25,
		outlierDelay: time.Second, seed: 42,
	}
	first, second := cfg.sampler(), cfg.sampler()
	seen := make(map[time.Duration]bool)
	outliers := 0
	for i := 0; i < 100; i++ {
		got := first()
		if want := second(); got != want {
			t.Fatalf("sample %d: got %v, want %v", i, got, want)
		}
		if got >= cfg.outlierDelay {
			got -= cfg.outlierDelay
			outliers++
		}
		if got < 0 || got > cfg.jitter {
			t.Fatalf("jitter outside configured range: %v", got)
		}
		seen[got] = true
	}
	if outliers == 0 || outliers == 100 || len(seen) < 2 {
		t.Fatalf("expected varied jitter and a mix of ordinary and outlier delays, got %d jitter values and %d outliers", len(seen), outliers)
	}
}
