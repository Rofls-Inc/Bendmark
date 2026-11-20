package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestTelemetryCountsWorkAndExcludesTechnicalRequests(t *testing.T) {
	handler := newHandler(config{mode: modeHealthy})
	initial := readTelemetry(t, handler)
	assertTelemetryCount(t, "completed_total", initial.CompletedTotal, 0)
	assertTelemetryCount(t, "rejected_total", initial.RejectedTotal, 0)
	if initial.WorkersBusy != nil || initial.WorkersTotal != nil ||
		initial.QueueLength != nil || initial.QueueCapacity != nil {
		t.Fatalf("healthy mode reported a pool or a queue: %+v", initial)
	}

	for _, tc := range []struct {
		method string
		path   string
		status int
	}{
		{http.MethodGet, "/health", http.StatusOK},
		{http.MethodHead, "/telemetry", http.StatusOK},
		{http.MethodPost, "/telemetry", http.StatusMethodNotAllowed},
		{http.MethodPost, "/health", http.StatusMethodNotAllowed},
		{http.MethodPost, "/work", http.StatusOK},
	} {
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, httptest.NewRequest(tc.method, tc.path, nil))
		if response.Code != tc.status {
			t.Fatalf("%s %s: got %d, want %d", tc.method, tc.path, response.Code, tc.status)
		}
		if tc.method == http.MethodHead && response.Body.Len() != 0 {
			t.Fatalf("HEAD %s returned a body", tc.path)
		}
	}

	final := readTelemetry(t, handler)
	assertTelemetryCount(t, "completed_total", final.CompletedTotal, 1)
	assertTelemetryCount(t, "rejected_total", final.RejectedTotal, 0)
	if !final.StartedAt.Equal(initial.StartedAt) || final.CapturedAt.Before(initial.CapturedAt) {
		t.Fatalf("timestamps changed unexpectedly: before=%+v after=%+v", initial, final)
	}
}

func TestTelemetryReadsFullQueueAndExcludesCanceledWork(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	handler := newHandler(config{mode: modeSaturation, workers: 1, queueSize: 1, serviceTime: time.Hour})
	initial := readTelemetry(t, handler)
	assertTelemetryCount(t, "queue_length", initial.QueueLength, 0)

	cancelActive, activeDone := startBlockedWork(t, ctx, handler)
	cancelQueued, queuedDone := startBlockedWork(t, ctx, handler)
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/work", nil).WithContext(ctx))
	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("overflow status = %d, want 503", response.Code)
	}

	full := readTelemetry(t, handler)
	assertTelemetryCount(t, "workers_busy", full.WorkersBusy, 1)
	assertTelemetryCount(t, "workers_total", full.WorkersTotal, 1)
	assertTelemetryCount(t, "queue_length", full.QueueLength, 1)
	assertTelemetryCount(t, "queue_capacity", full.QueueCapacity, 1)
	assertTelemetryCount(t, "completed_total", full.CompletedTotal, 0)
	assertTelemetryCount(t, "rejected_total", full.RejectedTotal, 1)

	cancelQueued()
	select {
	case <-queuedDone:
	case <-ctx.Done():
		t.Fatal("canceled waiter did not leave the queue")
	}
	dequeued := readTelemetry(t, handler)
	assertTelemetryCount(t, "queue_length", dequeued.QueueLength, 0)
	assertTelemetryCount(t, "workers_busy", dequeued.WorkersBusy, 1)
	assertTelemetryCount(t, "completed_total", dequeued.CompletedTotal, 0)
	assertTelemetryCount(t, "rejected_total", dequeued.RejectedTotal, 1)

	cancelActive()
	select {
	case <-activeDone:
	case <-ctx.Done():
		t.Fatal("canceled request did not release its worker")
	}
	drained := readTelemetry(t, handler)
	assertTelemetryCount(t, "workers_busy", drained.WorkersBusy, 0)
	assertTelemetryCount(t, "completed_total", drained.CompletedTotal, 0)
	assertTelemetryCount(t, "rejected_total", drained.RejectedTotal, 1)
}

func readTelemetry(t *testing.T, handler http.Handler) telemetrySnapshot {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	response := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodGet, "/telemetry", nil).WithContext(ctx)
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("Cache-Control") != "no-store" ||
		response.Header().Get("Content-Type") != "application/json; charset=utf-8" {
		t.Fatalf("telemetry response: %d %v", response.Code, response.Header())
	}

	var snapshot telemetrySnapshot
	if err := json.Unmarshal(response.Body.Bytes(), &snapshot); err != nil {
		t.Fatal(err)
	}
	if snapshot.SchemaVersion != 1 || snapshot.StartedAt.IsZero() || snapshot.StartedAt.Location() != time.UTC ||
		snapshot.CapturedAt.Location() != time.UTC || snapshot.CapturedAt.Before(snapshot.StartedAt) {
		t.Fatalf("invalid snapshot metadata: %s", response.Body.String())
	}
	return snapshot
}

func assertTelemetryCount(t *testing.T, field string, value *int64, want int64) {
	t.Helper()
	if value == nil {
		t.Fatalf("%s: got null, want %d", field, want)
	}
	if *value != want {
		t.Fatalf("%s: got %d, want %d", field, *value, want)
	}
}
