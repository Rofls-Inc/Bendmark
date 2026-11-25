package main

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestSwitchModeWithoutRestart(t *testing.T) {
	cfg, err := parseConfig([]string{"-mode=reject", "-workers=1", "-service-time=1h"}, func(string) string { return "" })
	if err != nil {
		t.Fatal(err)
	}
	handler := newHandler(cfg)
	before := readTelemetry(t, handler)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan struct{})
	go func() {
		defer close(done)
		handler.ServeHTTP(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, "/work", nil).WithContext(ctx))
	}()
	deadline := time.Now().Add(time.Second)
	for {
		snapshot := readTelemetry(t, handler)
		if *snapshot.WorkersBusy == 1 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("work did not acquire a slot")
		}
		time.Sleep(time.Millisecond)
	}

	next := cfg.settings()
	next.Mode, next.Workers, next.QueueSize = modeSaturation, 3, 16
	next.ServiceTime = "250ms"
	if response := requestSettings(t, handler, next); response.Code != http.StatusConflict {
		t.Fatalf("settings update during work: got %d", response.Code)
	}
	cancel()
	<-done

	response := requestSettings(t, handler, next)
	if response.Code != http.StatusOK {
		t.Fatalf("settings update: got %d, %s", response.Code, response.Body)
	}
	current := httptest.NewRecorder()
	handler.ServeHTTP(current, httptest.NewRequest(http.MethodGet, "/config", nil))
	var configuration labConfiguration
	if err := json.Unmarshal(current.Body.Bytes(), &configuration); err != nil {
		t.Fatal(err)
	}
	if configuration.Settings != next || configuration.CapacityRPS != 12 {
		t.Fatalf("unexpected configuration: %+v", configuration)
	}
	after := readTelemetry(t, handler)
	if !after.StartedAt.Equal(before.StartedAt) {
		t.Fatal("process start timestamp changed")
	}
	assertTelemetryCount(t, "workers_total", after.WorkersTotal, 3)
	assertTelemetryCount(t, "queue_capacity", after.QueueCapacity, 16)

	next.Workers = 0
	if response := requestSettings(t, handler, next); response.Code != http.StatusBadRequest {
		t.Fatalf("invalid settings update: got %d", response.Code)
	}
	work := httptest.NewRecorder()
	handler.ServeHTTP(work, httptest.NewRequest(http.MethodGet, "/work", nil))
	if work.Code != http.StatusOK || !strings.Contains(work.Body.String(), `"mode":"saturation"`) {
		t.Fatalf("workload did not use the new mode: %d %s", work.Code, work.Body)
	}
}

func requestSettings(t *testing.T, handler http.Handler, desired settings) *httptest.ResponseRecorder {
	t.Helper()
	body, err := json.Marshal(desired)
	if err != nil {
		t.Fatal(err)
	}
	request := httptest.NewRequest(http.MethodPut, "/config", bytes.NewReader(body))
	request.Header.Set("Content-Type", "application/json")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	return response
}
