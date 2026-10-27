package main

import (
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"time"
)

func main() {
	listen := flag.String("listen", "127.0.0.1:8081", "address to listen on (host:port)")
	upstream := flag.String("upstream", "", "destination address (host:port)")
	delay := flag.Duration("delay", 0, "delay before forwarding each response chunk, e.g. 100ms")
	jitter := flag.Duration("jitter", 0, "random extra delay in [0, jitter] for each response chunk")
	dropRate := flag.Float64("drop-rate", 0, "fraction of new TCP connections to close immediately (0..1)")
	resetAfter := flag.Duration("reset-after", 0, "abort a connection after this time from accept; 0 disables")
	seed := flag.Int64("seed", 1, "random seed for reproducible fault decisions")
	healthListen := flag.String("health-listen", "", "separate HTTP health address; empty disables")
	healthcheck := flag.String("healthcheck", "", "check an HTTP health URL and exit")
	flag.Parse()

	if *healthcheck != "" {
		if err := checkHealth(*healthcheck); err != nil {
			exitWithError("%v", err)
		}
		return
	}

	if *upstream == "" {
		exitWithError("-upstream is required")
	}
	if _, _, err := net.SplitHostPort(*upstream); err != nil {
		exitWithError("-upstream must be host:port: %v", err)
	}
	faults := faultConfig{delay: *delay, jitter: *jitter, dropRate: *dropRate, resetAfter: *resetAfter, seed: *seed}
	if err := faults.validate(); err != nil {
		exitWithError("%v", err)
	}

	listener, err := net.Listen("tcp", *listen)
	if err != nil {
		exitWithError("listen: %v", err)
	}
	defer listener.Close()

	if *healthListen != "" {
		server, err := startHealthServer(*healthListen)
		if err != nil {
			exitWithError("health listen: %v", err)
		}
		defer server.Close()
	}

	log.Printf("listening on %s, upstream %s, delay %s, jitter %s, drop-rate %g, reset-after %s, seed %d",
		listener.Addr(), *upstream, faults.delay, faults.jitter, faults.dropRate, faults.resetAfter, faults.seed)
	if err := serve(listener, *upstream, faults); err != nil {
		exitWithError("proxy: %v", err)
	}
}

func exitWithError(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "injector: "+format+"\n", args...)
	os.Exit(1)
}

const connectTimeout = 5 * time.Second
