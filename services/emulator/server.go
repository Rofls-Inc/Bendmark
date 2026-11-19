package main

import (
	"context"
	"fmt"
	"net/http"
	"time"
)

func newHandler(cfg config) http.Handler {
	sampleNoise := cfg.noise.sampler()
	var pool *workerPool
	if cfg.mode != modeHealthy {
		pool = &workerPool{slots: make(chan struct{}, cfg.workers)}
		if cfg.mode != modeReject {
			pool.waiting = make(chan struct{}, cfg.queueSize)
		}
	}

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet && r.URL.Path == "/health" {
			w.Header().Set("Content-Type", "text/plain; charset=utf-8")
			fmt.Fprintln(w, "ok")
			return
		}

		ctx := r.Context()
		if ctx.Err() != nil {
			return
		}

		queued := false
		if pool != nil {
			var err error
			queued, err = pool.acquire(ctx)
			if err == errPoolFull {
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(http.StatusServiceUnavailable)
				fmt.Fprintf(w, "{\"status\":\"busy\",\"mode\":%q}\n", cfg.mode)
				return
			}
			if err != nil {
				return
			}
			defer pool.release()
		}

		if ctx.Err() != nil || !waitForProcessing(ctx, cfg.serviceTime+sampleNoise()) {
			return
		}
		if queued && cfg.mode == modeSlow && !waitForProcessing(ctx, cfg.slowDelay) {
			return
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, "{\"status\":\"ok\",\"mode\":%q}\n", cfg.mode)
	})
}

func waitForProcessing(ctx context.Context, delay time.Duration) bool {
	if ctx.Err() != nil {
		return false
	}
	if delay == 0 {
		return true
	}
	timer := time.NewTimer(delay)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-timer.C:
		return ctx.Err() == nil
	}
}
