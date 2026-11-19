package main

import (
	"fmt"
	"net/http"
	"time"
)

func checkHealth(url string) error {
	client := http.Client{
		Timeout: 2 * time.Second,
		CheckRedirect: func(_ *http.Request, _ []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	response, err := client.Get(url)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return fmt.Errorf("expected HTTP 200, got %s", response.Status)
	}
	return nil
}
