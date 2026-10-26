package main

import (
	"fmt"
	"log"
	"net"
	"net/http"
	"time"
)

func startHealthServer(address string) (*http.Server, error) {
	listener, err := net.Listen("tcp", address)
	if err != nil {
		return nil, err
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /health", func(w http.ResponseWriter, _ *http.Request) {
		fmt.Fprint(w, "Healthy")
	})
	server := &http.Server{Addr: listener.Addr().String(), Handler: mux, ReadHeaderTimeout: 2 * time.Second}
	go func() {
		if err := server.Serve(listener); err != nil && err != http.ErrServerClosed {
			log.Printf("health server: %v", err)
		}
	}()
	return server, nil
}

func checkHealth(address string) error {
	client := http.Client{Timeout: 2 * time.Second}
	response, err := client.Get(address)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return fmt.Errorf("healthcheck: HTTP %d", response.StatusCode)
	}
	return nil
}
