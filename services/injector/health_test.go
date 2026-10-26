package main

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestHealthWorksWhenEveryProxyConnectionIsDropped(t *testing.T) {
	proxyURL := startProxy(t, "127.0.0.1:1", faultConfig{dropRate: 1})
	health, err := startHealthServer("127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { health.Close() })
	if err := checkHealth("http://" + health.Addr + "/health"); err != nil {
		t.Fatalf("healthcheck was affected by injected faults: %v", err)
	}
	if response, err := testClient().Get(proxyURL); err == nil {
		response.Body.Close()
		t.Fatal("drop-rate=1 allowed a request through")
	}
}

func TestHealthCheckRejectsUnhealthyStatus(t *testing.T) {
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
	}))
	t.Cleanup(upstream.Close)
	if err := checkHealth(upstream.URL); err == nil {
		t.Fatal("healthcheck accepted HTTP 503")
	}
}
