package main

import (
	"errors"
	"flag"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"strconv"
	"time"
)

const (
	modeHealthy = "healthy"
	modeReject  = "reject"
)

type config struct {
	listen      string
	mode        string
	serviceTime time.Duration
	workers     int
}

func main() {
	cfg, err := parseConfig(os.Args[1:], os.Getenv)
	if errors.Is(err, flag.ErrHelp) {
		return
	}
	if err != nil {
		log.Fatal(err)
	}

	server := &http.Server{
		Addr:              cfg.listen,
		Handler:           newHandler(cfg),
		ReadHeaderTimeout: 5 * time.Second,
	}
	log.Printf("listening on %s, mode %s, service-time %s", cfg.listen, cfg.mode, cfg.serviceTime)
	if cfg.mode == modeReject {
		log.Printf("workers %d, estimated capacity %.1f requests/s", cfg.workers, float64(cfg.workers)/cfg.serviceTime.Seconds())
	}
	if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatal(err)
	}
}

func parseConfig(args []string, getenv func(string) string) (config, error) {
	envOr := func(name, fallback string) string {
		if value := getenv(name); value != "" {
			return value
		}
		return fallback
	}
	flags := flag.NewFlagSet("emulator", flag.ContinueOnError)
	listen := flags.String("listen", envOr("EMULATOR_LISTEN", "127.0.0.1:8083"), "HTTP listen address (host:port)")
	mode := flags.String("mode", envOr("EMULATOR_MODE", modeHealthy), "emulation mode (healthy or reject)")
	serviceTime := flags.String("service-time", envOr("EMULATOR_SERVICE_TIME", "50ms"), "time spent processing each request, e.g. 50ms")
	workers := flags.String("workers", envOr("EMULATOR_WORKERS", "4"), "maximum requests processed concurrently in reject mode")
	if err := flags.Parse(args); err != nil {
		return config{}, err
	}
	if flags.NArg() != 0 {
		return config{}, fmt.Errorf("unexpected positional arguments: %v", flags.Args())
	}
	if *mode != modeHealthy && *mode != modeReject {
		return config{}, fmt.Errorf("unsupported mode %q: use healthy or reject", *mode)
	}
	if _, _, err := net.SplitHostPort(*listen); err != nil {
		return config{}, fmt.Errorf("-listen must be host:port: %w", err)
	}
	duration, err := time.ParseDuration(*serviceTime)
	if err != nil {
		return config{}, fmt.Errorf("invalid -service-time: %w", err)
	}
	if duration < 0 {
		return config{}, fmt.Errorf("-service-time must not be negative")
	}
	if *mode == modeReject && duration == 0 {
		return config{}, fmt.Errorf("-service-time must be positive in reject mode")
	}
	workerCount, err := strconv.Atoi(*workers)
	if err != nil || workerCount <= 0 {
		return config{}, fmt.Errorf("-workers must be a positive integer")
	}
	return config{listen: *listen, mode: *mode, serviceTime: duration, workers: workerCount}, nil
}
