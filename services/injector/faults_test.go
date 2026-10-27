package main

import (
	"bytes"
	"context"
	"errors"
	"math"
	"math/rand"
	"testing"
	"time"
)

func TestFaultConfigValidation(t *testing.T) {
	cases := []struct {
		name   string
		config faultConfig
		valid  bool
	}{
		{"zero", faultConfig{}, true},
		{"all drops", faultConfig{dropRate: 1}, true},
		{"max jitter", faultConfig{jitter: maxDuration}, true},
		{"negative delay", faultConfig{delay: -1}, false},
		{"negative jitter", faultConfig{jitter: -1}, false},
		{"negative reset", faultConfig{resetAfter: -1}, false},
		{"negative drop rate", faultConfig{dropRate: -0.1}, false},
		{"drop rate above one", faultConfig{dropRate: 1.1}, false},
		{"nan", faultConfig{dropRate: math.NaN()}, false},
		{"infinite", faultConfig{dropRate: math.Inf(1)}, false},
		{"delay overflow", faultConfig{delay: maxDuration, jitter: 1}, false},
	}
	for _, test := range cases {
		t.Run(test.name, func(t *testing.T) {
			if err := test.config.validate(); (err == nil) != test.valid {
				t.Fatalf("validate() = %v, want valid=%v", err, test.valid)
			}
		})
	}
}

func TestJitterBoundsAndReproducibleSeed(t *testing.T) {
	faults := faultConfig{delay: 30 * time.Millisecond, jitter: 20 * time.Millisecond}
	first := rand.New(rand.NewSource(42))
	second := rand.New(rand.NewSource(42))
	seen := make(map[time.Duration]bool)
	for i := 0; i < 2000; i++ {
		delay := faults.responseDelay(first)
		if delay < faults.delay || delay > faults.delay+faults.jitter {
			t.Fatalf("delay %s outside [%s, %s]", delay, faults.delay, faults.delay+faults.jitter)
		}
		if repeated := faults.responseDelay(second); delay != repeated {
			t.Fatalf("same seed produced %s and %s", delay, repeated)
		}
		seen[delay] = true
	}
	if len(seen) < 100 {
		t.Fatalf("jitter has insufficient variation: %d distinct values", len(seen))
	}
}

func TestZeroAndMaximumJitter(t *testing.T) {
	if delay := (faultConfig{delay: 80 * time.Millisecond}).responseDelay(nil); delay != 80*time.Millisecond {
		t.Fatalf("zero jitter changed delay to %s", delay)
	}
	random := rand.New(rand.NewSource(1))
	for i := 0; i < 100; i++ {
		if delay := (faultConfig{jitter: maxDuration}).responseDelay(random); delay < 0 {
			t.Fatalf("maximum jitter overflowed: %s", delay)
		}
	}
}

func TestResetCancelsPendingResponseDelay(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	defer cancel()
	var destination bytes.Buffer
	writer := delayedWriter{destination: &destination, faults: faultConfig{delay: time.Second}, context: ctx}
	started := time.Now()
	n, err := writer.Write([]byte("response"))
	if !errors.Is(err, context.DeadlineExceeded) || n != 0 || destination.Len() != 0 {
		t.Fatalf("cancelled write = %d, %v, destination=%q", n, err, destination.String())
	}
	if elapsed := time.Since(started); elapsed > 750*time.Millisecond {
		t.Fatalf("delay did not stop promptly: %s", elapsed)
	}
}
