package main

import (
	"encoding/json"
	"net/http"
	"sync/atomic"
	"time"
)

type telemetrySnapshot struct {
	SchemaVersion  int       `json:"schema_version"`
	CapturedAt     time.Time `json:"captured_at"`
	StartedAt      time.Time `json:"started_at"`
	WorkersBusy    *int64    `json:"workers_busy"`
	WorkersTotal   *int64    `json:"workers_total"`
	QueueLength    *int64    `json:"queue_length"`
	QueueCapacity  *int64    `json:"queue_capacity"`
	CompletedTotal *int64    `json:"completed_total"`
	RejectedTotal  *int64    `json:"rejected_total"`
}

type telemetryCounters struct {
	startedAt time.Time
	completed atomic.Int64
	rejected  atomic.Int64
}

func (c *telemetryCounters) snapshot(pool *workerPool) telemetrySnapshot {
	completed, rejected := c.completed.Load(), c.rejected.Load()
	snapshot := telemetrySnapshot{
		SchemaVersion:  1,
		CapturedAt:     time.Now().UTC(),
		StartedAt:      c.startedAt,
		CompletedTotal: &completed,
		RejectedTotal:  &rejected,
	}
	if pool != nil {
		busy, total := int64(len(pool.slots)), int64(cap(pool.slots))
		snapshot.WorkersBusy, snapshot.WorkersTotal = &busy, &total
		if pool.waiting != nil {
			length, capacity := int64(len(pool.waiting)), int64(cap(pool.waiting))
			snapshot.QueueLength, snapshot.QueueCapacity = &length, &capacity
		}
	}
	return snapshot
}

func (c *telemetryCounters) serve(w http.ResponseWriter, r *http.Request, pool *workerPool) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	if r.Method == http.MethodHead {
		return
	}
	json.NewEncoder(w).Encode(c.snapshot(pool))
}
