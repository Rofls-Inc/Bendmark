package main

import (
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func startProxy(t *testing.T, upstream string, delay time.Duration) string {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- serve(listener, upstream, delay) }()
	t.Cleanup(func() {
		listener.Close()
		if err := <-done; err != nil {
			t.Errorf("proxy stopped: %v", err)
		}
	})
	return "http://" + listener.Addr().String()
}

func testClient() *http.Client {
	return &http.Client{
		Timeout:   2 * time.Second,
		Transport: &http.Transport{DisableKeepAlives: true},
	}
}

func TestProxyForwardsRequestAndResponse(t *testing.T) {
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost || r.URL.Path != "/orders" {
			t.Errorf("unexpected request: %s %s", r.Method, r.URL.Path)
		}
		body, err := io.ReadAll(r.Body)
		if err != nil {
			t.Error(err)
		}
		w.WriteHeader(http.StatusCreated)
		w.Write(body)
	}))
	t.Cleanup(upstream.Close)
	proxyURL := startProxy(t, strings.TrimPrefix(upstream.URL, "http://"), 0)

	response, err := testClient().Post(proxyURL+"/orders", "text/plain", strings.NewReader("buy"))
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != http.StatusCreated || string(body) != "buy" {
		t.Fatalf("unexpected response: %d %q", response.StatusCode, body)
	}
}

func TestProxyDelaysResponse(t *testing.T) {
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Write([]byte("ok"))
	}))
	t.Cleanup(upstream.Close)
	proxyURL := startProxy(t, strings.TrimPrefix(upstream.URL, "http://"), 80*time.Millisecond)

	started := time.Now()
	response, err := testClient().Get(proxyURL)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if elapsed := time.Since(started); elapsed < 70*time.Millisecond {
		t.Fatalf("response arrived too early: %s", elapsed)
	}
}
