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
	modeHealthy    = "healthy"
	modeReject     = "reject"
	modeSaturation = "saturation"
	modeSlow       = "slow"
)

type config struct {
	healthcheck string
	listen      string
	mode        string
	serviceTime time.Duration
	workers     int
	queueSize   int
	slowDelay   time.Duration
	noise       noiseConfig
}

func main() {
	cfg, err := parseConfig(os.Args[1:], os.Getenv)
	if errors.Is(err, flag.ErrHelp) {
		return
	}
	if err != nil {
		log.Fatal(err)
	}
	if cfg.healthcheck != "" {
		if err := checkHealth(cfg.healthcheck); err != nil {
			log.Fatalf("healthcheck: %v", err)
		}
		return
	}

	server := &http.Server{
		Addr:              cfg.listen,
		Handler:           newHandler(cfg),
		ReadHeaderTimeout: 5 * time.Second,
	}
	log.Printf("listening on %s, mode %s, service-time %s", cfg.listen, cfg.mode, cfg.serviceTime)
	if cfg.mode != modeHealthy {
		log.Printf("workers %d, estimated capacity before overload %.1f requests/s", cfg.workers, float64(cfg.workers)/cfg.serviceTime.Seconds())
	}
	if cfg.mode == modeSaturation || cfg.mode == modeSlow {
		log.Printf("queue size %d", cfg.queueSize)
	}
	if cfg.mode == modeSlow {
		log.Printf("extra delay for queued requests %s", cfg.slowDelay)
	}
	if cfg.noise.jitter > 0 || cfg.noise.outlierRate > 0 {
		log.Printf("jitter %s, outlier rate %g, outlier delay %s, seed %d",
			cfg.noise.jitter, cfg.noise.outlierRate, cfg.noise.outlierDelay, cfg.noise.seed)
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
	healthcheck := flags.String("healthcheck", "", "check an emulator health URL and exit")
	listen := flags.String("listen", envOr("EMULATOR_LISTEN", "127.0.0.1:8083"), "HTTP listen address (host:port)")
	mode := flags.String("mode", envOr("EMULATOR_MODE", modeHealthy), "emulation mode (healthy, reject, saturation or slow)")
	serviceTime := flags.String("service-time", envOr("EMULATOR_SERVICE_TIME", "50ms"), "time spent processing each request, e.g. 50ms")
	workers := flags.String("workers", envOr("EMULATOR_WORKERS", "4"), "maximum requests processed concurrently in pool modes")
	queueSize := flags.String("queue-size", envOr("EMULATOR_QUEUE_SIZE", "32"), "maximum waiting requests in saturation and slow modes")
	slowDelay := flags.String("slow-delay", envOr("EMULATOR_SLOW_DELAY", "5s"), "extra processing time for a queued request in slow mode")
	jitter := flags.String("jitter", envOr("EMULATOR_JITTER", "0s"), "maximum random extra processing time per accepted request")
	outlierRate := flags.String("outlier-rate", envOr("EMULATOR_OUTLIER_RATE", "0"), "probability of an extra outlier delay per accepted request (0..1)")
	outlierDelay := flags.String("outlier-delay", envOr("EMULATOR_OUTLIER_DELAY", "0s"), "extra processing time for an outlier")
	seed := flags.String("seed", envOr("EMULATOR_SEED", "1"), "seed for repeatable latency samples")
	if err := flags.Parse(args); err != nil {
		return config{}, err
	}
	if flags.NArg() != 0 {
		return config{}, fmt.Errorf("unexpected positional arguments: %v", flags.Args())
	}
	if *healthcheck != "" {
		return config{healthcheck: *healthcheck}, nil
	}
	if _, _, err := net.SplitHostPort(*listen); err != nil {
		return config{}, fmt.Errorf("-listen must be host:port: %w", err)
	}
	workerCount, err := strconv.Atoi(*workers)
	if err != nil {
		return config{}, fmt.Errorf("-workers must be a positive integer")
	}
	queue, err := strconv.Atoi(*queueSize)
	if err != nil {
		return config{}, fmt.Errorf("-queue-size must be a positive integer")
	}
	cfg, err := (settings{
		Mode: *mode, ServiceTime: *serviceTime, Workers: workerCount, QueueSize: queue,
		SlowDelay: *slowDelay, Jitter: *jitter, OutlierRate: *outlierRate, OutlierDelay: *outlierDelay,
		Seed: *seed,
	}).config()
	cfg.listen = *listen
	return cfg, err
}
