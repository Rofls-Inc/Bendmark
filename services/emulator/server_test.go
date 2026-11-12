package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestHealthyAcceptsMethodsAndPaths(t *testing.T) {
	handler := newHandler(0)
	for _, method := range []string{"GET", "POST", "PUT", "PATCH", "DELETE"} {
		t.Run(method, func(t *testing.T) {
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, httptest.NewRequest(method, "/orders?asset=ABS", nil))
			if response.Code != http.StatusOK {
				t.Fatalf("status = %d, want 200", response.Code)
			}
			var body struct {
				Status string `json:"status"`
				Mode   string `json:"mode"`
			}
			if err := json.Unmarshal(response.Body.Bytes(), &body); err != nil {
				t.Fatal(err)
			}
			if body.Status != "ok" || body.Mode != "healthy" {
				t.Fatalf("unexpected response: %s", response.Body.String())
			}
		})
	}
}

func TestHealthBypassesProcessingDelay(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	response := httptest.NewRecorder()
	request := httptest.NewRequest("GET", "/health", nil).WithContext(ctx)
	newHandler(time.Hour).ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Body.String() != "ok\n" {
		t.Fatalf("health entered the workload handler: %d %q", response.Code, response.Body.String())
	}
}

func TestHealthyAppliesProcessingDelay(t *testing.T) {
	const delay = 30 * time.Millisecond
	response := httptest.NewRecorder()
	started := time.Now()
	newHandler(delay).ServeHTTP(response, httptest.NewRequest("GET", "/work", nil))
	// Проверяем грубую нижнюю границу, а не точность таймеров ОС.
	if elapsed := time.Since(started); elapsed < delay/2 {
		t.Fatalf("processing delay was skipped: elapsed %s", elapsed)
	}
}

func TestCanceledRequestDoesNotWaitOrWriteResponse(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	response := httptest.NewRecorder()
	request := httptest.NewRequest("POST", "/work", nil).WithContext(ctx)
	done := make(chan struct{})
	go func() {
		newHandler(time.Hour).ServeHTTP(response, request)
		close(done)
	}()
	select {
	case <-done:
		if response.Body.Len() != 0 {
			t.Fatalf("wrote a response after cancellation: %q", response.Body.String())
		}
	case <-time.After(3 * time.Second):
		t.Fatal("request cancellation did not interrupt the delay")
	}
}

func TestConfigFlagsOverrideEnvironment(t *testing.T) {
	env := map[string]string{
		"EMULATOR_LISTEN":       "127.0.0.1:9000",
		"EMULATOR_SERVICE_TIME": "invalid",
		"EMULATOR_MODE":         "healthy",
	}
	cfg, err := parseConfig([]string{"-service-time=10ms"}, func(key string) string { return env[key] })
	if err != nil {
		t.Fatal(err)
	}
	if cfg.listen != "127.0.0.1:9000" || cfg.serviceTime != 10*time.Millisecond || cfg.mode != "healthy" {
		t.Fatalf("unexpected config: %+v", cfg)
	}
}

func TestInvalidConfigIsRejected(t *testing.T) {
	for _, argument := range []string{"-mode=unknown", "-service-time=invalid", "-service-time=-1s", "-listen=invalid"} {
		t.Run(argument, func(t *testing.T) {
			if _, err := parseConfig([]string{argument}, func(string) string { return "" }); err == nil {
				t.Fatalf("accepted invalid argument: %s", argument)
			}
		})
	}
}
