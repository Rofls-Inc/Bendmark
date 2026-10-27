package main

import (
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"
)

func startEchoServer(t *testing.T) string {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	var handlers sync.WaitGroup
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			connection, err := listener.Accept()
			if err != nil {
				return
			}
			handlers.Add(1)
			go func() {
				defer handlers.Done()
				defer connection.Close()
				connection.SetDeadline(time.Now().Add(2 * time.Second))
				var message [1]byte
				if _, err := io.ReadFull(connection, message[:]); err == nil {
					connection.Write(message[:])
				}
			}()
		}
	}()
	t.Cleanup(func() {
		listener.Close()
		<-done
		handlers.Wait()
	})
	return listener.Addr().String()
}

func echoSucceeds(address string) bool {
	connection, err := net.DialTimeout("tcp", address, time.Second)
	if err != nil {
		return false
	}
	defer connection.Close()
	connection.SetDeadline(time.Now().Add(time.Second))
	if _, err := connection.Write([]byte("x")); err != nil {
		return false
	}
	var response [1]byte
	_, err = io.ReadFull(connection, response[:])
	return err == nil && response[0] == 'x'
}

func TestDropRateOnNewConnections(t *testing.T) {
	upstream := startEchoServer(t)
	proxyURL := startProxy(t, upstream, faultConfig{dropRate: 0.35, seed: 42})
	address := strings.TrimPrefix(proxyURL, "http://")
	const attempts = 1000
	dropped := 0
	for i := 0; i < attempts; i++ {
		if !echoSucceeds(address) {
			dropped++
		}
	}
	rate := float64(dropped) / attempts
	t.Logf("dropped %d/%d connections (%.3f)", dropped, attempts, rate)
	if rate < 0.29 || rate > 0.41 {
		t.Fatalf("dropped %d/%d connections (%.3f), want about 0.35", dropped, attempts, rate)
	}
}

func TestDropRateEndpoints(t *testing.T) {
	upstream := startEchoServer(t)
	for _, rate := range []float64{0, 1} {
		proxyURL := startProxy(t, upstream, faultConfig{dropRate: rate})
		for i := 0; i < 8; i++ {
			if success := echoSucceeds(strings.TrimPrefix(proxyURL, "http://")); success != (rate == 0) {
				t.Fatalf("drop-rate=%g, success=%v", rate, success)
			}
		}
	}
}

func TestDropSeedIsIndependentOfResponseJitter(t *testing.T) {
	upstream := startEchoServer(t)
	sample := func(seed int64, jitter time.Duration) []bool {
		proxyURL := startProxy(t, upstream, faultConfig{dropRate: 0.35, seed: seed, jitter: jitter})
		outcomes := make([]bool, 64)
		for i := range outcomes {
			outcomes[i] = echoSucceeds(strings.TrimPrefix(proxyURL, "http://"))
		}
		return outcomes
	}
	first := sample(42, 0)
	if !slices.Equal(first, sample(42, time.Millisecond)) {
		t.Fatal("response jitter changed the same seed's connection drop sequence")
	}
	if slices.Equal(first, sample(7, 0)) {
		t.Fatal("different seeds produced the same drop sequence")
	}
}

func TestConnectionResetAfterDeadline(t *testing.T) {
	upstream := httptest.NewServer(http.HandlerFunc(func(_ http.ResponseWriter, r *http.Request) {
		<-r.Context().Done()
	}))
	t.Cleanup(upstream.Close)
	proxyURL := startProxy(t, strings.TrimPrefix(upstream.URL, "http://"), faultConfig{resetAfter: 80 * time.Millisecond})
	started := time.Now()
	response, err := testClient().Get(proxyURL)
	if response != nil {
		response.Body.Close()
	}
	if err == nil {
		t.Fatal("connection was not reset")
	}
	if elapsed := time.Since(started); elapsed < 60*time.Millisecond || elapsed > time.Second {
		t.Fatalf("reset occurred after %s, want about 80ms", elapsed)
	}
}
