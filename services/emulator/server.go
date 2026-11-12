package main

import (
	"fmt"
	"net/http"
	"time"
)

func newHandler(serviceTime time.Duration) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet && r.URL.Path == "/health" {
			w.Header().Set("Content-Type", "text/plain; charset=utf-8")
			fmt.Fprintln(w, "ok")
			return
		}

		// Отмена запроса освобождает обработчик, не дожидаясь всей задержки.
		if serviceTime > 0 {
			timer := time.NewTimer(serviceTime)
			defer timer.Stop()
			select {
			case <-r.Context().Done():
				return
			case <-timer.C:
			}
		}
		if r.Context().Err() != nil {
			return
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintln(w, `{"status":"ok","mode":"healthy"}`)
	})
}
