package main

import (
	"fmt"
	"net/http"
	"time"
)

func newHandler(cfg config) http.Handler {
	var slots chan struct{}
	if cfg.mode == modeReject {
		slots = make(chan struct{}, cfg.workers)
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

		if slots != nil {
			select {
			case slots <- struct{}{}:
				defer func() { <-slots }()
			default:
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(http.StatusServiceUnavailable)
				fmt.Fprintln(w, `{"status":"busy","mode":"reject"}`)
				return
			}
		}

		if cfg.serviceTime > 0 {
			timer := time.NewTimer(cfg.serviceTime)
			defer timer.Stop()
			select {
			case <-ctx.Done():
				return
			case <-timer.C:
			}
		}
		if ctx.Err() != nil {
			return
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, "{\"status\":\"ok\",\"mode\":%q}\n", cfg.mode)
	})
}
