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
	flag.Parse()

	if *upstream == "" {
		exitWithError("-upstream is required")
	}
	if _, _, err := net.SplitHostPort(*upstream); err != nil {
		exitWithError("-upstream must be host:port: %v", err)
	}
	if *delay < 0 {
		exitWithError("-delay cannot be negative")
	}

	listener, err := net.Listen("tcp", *listen)
	if err != nil {
		exitWithError("listen: %v", err)
	}
	defer listener.Close()

	log.Printf("listening on %s, forwarding to %s, response delay %s", listener.Addr(), *upstream, *delay)
	if err := serve(listener, *upstream, *delay); err != nil {
		exitWithError("proxy: %v", err)
	}
}

func exitWithError(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "injector: "+format+"\n", args...)
	os.Exit(1)
}

const connectTimeout = 5 * time.Second
