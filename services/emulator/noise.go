package main

import (
	"fmt"
	"math"
	"math/rand/v2"
	"strconv"
	"sync"
	"time"
)

type noiseConfig struct {
	jitter       time.Duration
	outlierRate  float64
	outlierDelay time.Duration
	seed         int64
}

func parseNoise(jitter, rate, delay, seed string, serviceTime time.Duration) (noiseConfig, error) {
	spread, err := time.ParseDuration(jitter)
	if err != nil || spread < 0 {
		return noiseConfig{}, fmt.Errorf("-jitter must be a non-negative duration")
	}
	probability, err := strconv.ParseFloat(rate, 64)
	if err != nil || math.IsNaN(probability) || probability < 0 || probability > 1 {
		return noiseConfig{}, fmt.Errorf("-outlier-rate must be a number between 0 and 1")
	}
	outlier, err := time.ParseDuration(delay)
	if err != nil || outlier < 0 || (probability > 0 && outlier == 0) {
		return noiseConfig{}, fmt.Errorf("-outlier-delay must be non-negative and positive when -outlier-rate is enabled")
	}
	seedValue, err := strconv.ParseInt(seed, 10, 64)
	if err != nil {
		return noiseConfig{}, fmt.Errorf("-seed must be a signed 64-bit integer")
	}

	const maxDuration = time.Duration(math.MaxInt64)
	remaining := maxDuration - serviceTime
	if spread > remaining || (probability > 0 && outlier > remaining-spread) {
		return noiseConfig{}, fmt.Errorf("-service-time plus enabled noise exceeds the maximum duration")
	}
	return noiseConfig{jitter: spread, outlierRate: probability, outlierDelay: outlier, seed: seedValue}, nil
}

func (cfg noiseConfig) sampler() func() time.Duration {
	if cfg.jitter == 0 && cfg.outlierRate == 0 {
		return func() time.Duration { return 0 }
	}
	random := rand.New(rand.NewPCG(uint64(cfg.seed), 0))
	var mu sync.Mutex
	return func() time.Duration {
		mu.Lock()
		defer mu.Unlock()

		var extra time.Duration
		if cfg.jitter > 0 {
			extra = time.Duration(random.Uint64N(uint64(cfg.jitter) + 1))
		}
		if cfg.outlierRate > 0 && random.Float64() < cfg.outlierRate {
			extra += cfg.outlierDelay
		}
		return extra
	}
}
