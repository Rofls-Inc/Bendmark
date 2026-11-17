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
	handler := newHandler(config{mode: modeHealthy})
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
	newHandler(config{mode: modeHealthy, serviceTime: time.Hour}).ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Body.String() != "ok\n" {
		t.Fatalf("health entered the workload handler: %d %q", response.Code, response.Body.String())
	}
}

func TestHealthyAppliesProcessingDelay(t *testing.T) {
	const delay = 30 * time.Millisecond
	response := httptest.NewRecorder()
	started := time.Now()
	newHandler(config{mode: modeHealthy, serviceTime: delay}).ServeHTTP(response, httptest.NewRequest("GET", "/work", nil))
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
		newHandler(config{mode: modeHealthy, serviceTime: time.Hour}).ServeHTTP(response, request)
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

type observedContext struct {
	context.Context
	processing chan struct{}
}

func (ctx observedContext) Done() <-chan struct{} {
	select {
	case ctx.processing <- struct{}{}:
	default:
	}
	return ctx.Context.Done()
}

func TestRejectReturns503WhenFullAndReleasesSlotOnCancel(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	handler := newHandler(config{mode: modeReject, workers: 1, serviceTime: time.Hour})

	startRequest := func() (context.CancelFunc, <-chan struct{}) {
		t.Helper()
		requestContext, cancelRequest := context.WithCancel(ctx)
		t.Cleanup(cancelRequest)
		processing := make(chan struct{}, 1)
		request := httptest.NewRequest(http.MethodGet, "/work", nil).
			WithContext(observedContext{Context: requestContext, processing: processing})
		finished := make(chan struct{})
		go func() {
			handler.ServeHTTP(httptest.NewRecorder(), request)
			close(finished)
		}()

		select {
		case <-processing:
		case <-finished:
			t.Fatal("request finished before entering the pool")
		case <-ctx.Done():
			t.Fatal("request did not enter the pool")
		}
		return cancelRequest, finished
	}
	cancelFirst, firstFinished := startRequest()

	for _, tc := range []struct {
		path string
		want int
	}{
		{"/work", http.StatusServiceUnavailable},
		{"/health", http.StatusOK},
	} {
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, httptest.NewRequest(http.MethodGet, tc.path, nil).WithContext(ctx))
		if response.Code != tc.want || response.Body.Len() == 0 {
			t.Fatalf("%s: got %d %q, want %d with a response body", tc.path, response.Code, response.Body.String(), tc.want)
		}
	}

	cancelFirst()
	select {
	case <-firstFinished:
	case <-ctx.Done():
		t.Fatal("canceled request did not return")
	}
	cancelNext, nextFinished := startRequest()
	cancelNext()
	select {
	case <-nextFinished:
	case <-ctx.Done():
		t.Fatal("second canceled request did not return")
	}
}

func TestConfigFlagsOverrideEnvironment(t *testing.T) {
	env := map[string]string{
		"EMULATOR_LISTEN":       "127.0.0.1:9000",
		"EMULATOR_SERVICE_TIME": "invalid",
		"EMULATOR_MODE":         "reject",
		"EMULATOR_WORKERS":      "2",
	}
	cfg, err := parseConfig([]string{"-service-time=10ms", "-workers=3"}, func(key string) string { return env[key] })
	if err != nil {
		t.Fatal(err)
	}
	if cfg.listen != "127.0.0.1:9000" || cfg.serviceTime != 10*time.Millisecond || cfg.mode != modeReject || cfg.workers != 3 {
		t.Fatalf("unexpected config: %+v", cfg)
	}
}

func TestInvalidConfigIsRejected(t *testing.T) {
	for _, args := range [][]string{
		{"-mode=unknown"}, {"-service-time=invalid"}, {"-service-time=-1s"}, {"-listen=invalid"},
		{"-workers=0"}, {"-workers=-1"}, {"-workers=abc"}, {"-mode=reject", "-service-time=0s"},
	} {
		t.Run(args[0], func(t *testing.T) {
			if _, err := parseConfig(args, func(string) string { return "" }); err == nil {
				t.Fatalf("accepted invalid arguments: %v", args)
			}
		})
	}
}
