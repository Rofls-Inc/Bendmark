package main

import (
	"fmt"
	"math"
	"math/rand"
	"time"
)

const maxDuration = time.Duration(1<<63 - 1)

type faultConfig struct {
	delay      time.Duration
	jitter     time.Duration
	dropRate   float64
	resetAfter time.Duration
	seed       int64
}

func (config faultConfig) validate() error {
	if config.delay < 0 || config.jitter < 0 || config.resetAfter < 0 {
		return fmt.Errorf("-delay, -jitter and -reset-after cannot be negative")
	}
	if config.jitter > maxDuration-config.delay {
		return fmt.Errorf("-delay + -jitter exceeds the maximum duration")
	}
	if math.IsNaN(config.dropRate) || math.IsInf(config.dropRate, 0) || config.dropRate < 0 || config.dropRate > 1 {
		return fmt.Errorf("-drop-rate must be a finite number between 0 and 1")
	}
	return nil
}

func (config faultConfig) responseDelay(random *rand.Rand) time.Duration {
	if config.jitter == 0 {
		return config.delay
	}
	if config.jitter == maxDuration {
		return time.Duration(random.Int63())
	}
	return config.delay + time.Duration(random.Int63n(int64(config.jitter)+1))
}
