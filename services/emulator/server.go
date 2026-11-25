package main

import (
	"context"
	"fmt"
	"net/http"
	"sync"
	"time"
)

type emulator struct {
	mu        sync.Mutex
	active    int
	workload  workload
	telemetry telemetryCounters
}

type workload struct {
	cfg         config
	pool        *workerPool
	sampleNoise func() time.Duration
}

func newWorkload(cfg config) workload {
	var pool *workerPool
	if cfg.mode != modeHealthy {
		pool = &workerPool{slots: make(chan struct{}, cfg.workers)}
		if cfg.mode != modeReject {
			pool.waiting = make(chan struct{}, cfg.queueSize)
		}
	}
	return workload{cfg: cfg, pool: pool, sampleNoise: cfg.noise.sampler()}
}

func newHandler(cfg config) http.Handler {
	e := &emulator{workload: newWorkload(cfg), telemetry: telemetryCounters{startedAt: time.Now().UTC()}}
	technical := http.NewServeMux()
	technical.HandleFunc("GET /health", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		fmt.Fprintln(w, "ok")
	})
	technical.HandleFunc("GET /telemetry", func(w http.ResponseWriter, r *http.Request) {
		e.mu.Lock()
		pool := e.workload.pool
		e.mu.Unlock()
		e.telemetry.serve(w, r, pool)
	})
	technical.HandleFunc("GET /config", e.getSettings)
	technical.HandleFunc("PUT /config", e.putSettings)

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/health", "/telemetry", "/config":
			technical.ServeHTTP(w, r)
			return
		}

		e.mu.Lock()
		current := e.workload
		e.active++
		e.mu.Unlock()
		defer func() {
			e.mu.Lock()
			e.active--
			e.mu.Unlock()
		}()
		current.serve(w, r, &e.telemetry)
	})
}

func (work workload) serve(w http.ResponseWriter, r *http.Request, telemetry *telemetryCounters) {
	ctx := r.Context()
	if ctx.Err() != nil {
		return
	}

	queued := false
	if work.pool != nil {
		var err error
		queued, err = work.pool.acquire(ctx)
		if err == errPoolFull {
			telemetry.rejected.Add(1)
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusServiceUnavailable)
			fmt.Fprintf(w, "{\"status\":\"busy\",\"mode\":%q}\n", work.cfg.mode)
			return
		}
		if err != nil {
			return
		}
		defer work.pool.release()
	}

	if ctx.Err() != nil || !waitForProcessing(ctx, work.cfg.serviceTime+work.sampleNoise()) {
		return
	}
	if queued && work.cfg.mode == modeSlow && !waitForProcessing(ctx, work.cfg.slowDelay) {
		return
	}
	w.Header().Set("Content-Type", "application/json")
	if _, err := fmt.Fprintf(w, "{\"status\":\"ok\",\"mode\":%q}\n", work.cfg.mode); err == nil {
		telemetry.completed.Add(1)
	}
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
