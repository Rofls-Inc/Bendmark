package main

import (
	"context"
	"errors"
	"io"
	"log"
	"math/rand"
	"net"
	"sync"
	"time"
)

func serve(listener net.Listener, upstreamAddress string, faults faultConfig) error {
	if err := faults.validate(); err != nil {
		return err
	}
	// Decisions belong to the accept loop; response goroutines never share its RNG.
	random := rand.New(rand.NewSource(faults.seed))
	for {
		client, err := listener.Accept()
		if errors.Is(err, net.ErrClosed) {
			return nil
		}
		if err != nil {
			return err
		}
		acceptedAt := time.Now()
		if random.Float64() < faults.dropRate {
			abortConnection(client)
			continue
		}
		connectionRandom := rand.New(rand.NewSource(random.Int63()))
		go forwardConnection(client, upstreamAddress, faults, connectionRandom, acceptedAt)
	}
}

func forwardConnection(client net.Conn, upstreamAddress string, faults faultConfig, random *rand.Rand, acceptedAt time.Time) {
	defer client.Close()

	var ctx context.Context
	var cancel context.CancelFunc
	if faults.resetAfter > 0 {
		ctx, cancel = context.WithDeadline(context.Background(), acceptedAt.Add(faults.resetAfter))
	} else {
		ctx, cancel = context.WithCancel(context.Background())
	}
	defer cancel()

	dialer := net.Dialer{Timeout: connectTimeout}
	upstream, err := dialer.DialContext(ctx, "tcp", upstreamAddress)
	if err != nil {
		log.Printf("connect to %s: %v", upstreamAddress, err)
		return
	}
	defer upstream.Close()
	stopAbort := context.AfterFunc(ctx, func() {
		abortConnection(client)
		abortConnection(upstream)
	})
	defer stopAbort()

	var copies sync.WaitGroup
	copies.Add(2)
	go func() {
		defer copies.Done()
		if _, err := io.Copy(upstream, client); err != nil {
			cancel()
		}
		closeWrite(upstream)
	}()
	go func() {
		defer copies.Done()
		writer := delayedWriter{destination: client, faults: faults, random: random, context: ctx}
		if _, err := io.Copy(writer, upstream); err != nil {
			cancel()
		}
		closeWrite(client)
	}()
	copies.Wait()
}

func abortConnection(connection net.Conn) {
	if tcp, ok := connection.(*net.TCPConn); ok {
		tcp.SetLinger(0)
	}
	connection.Close()
}

func closeWrite(connection net.Conn) {
	if tcp, ok := connection.(*net.TCPConn); ok {
		tcp.CloseWrite()
	}
}

type delayedWriter struct {
	destination io.Writer
	faults      faultConfig
	random      *rand.Rand
	context     context.Context
}

func (writer delayedWriter) Write(chunk []byte) (int, error) {
	if delay := writer.faults.responseDelay(writer.random); delay > 0 {
		timer := time.NewTimer(delay)
		defer timer.Stop()
		select {
		case <-timer.C:
		case <-writer.context.Done():
			return 0, writer.context.Err()
		}
	}
	if err := writer.context.Err(); err != nil {
		return 0, err
	}
	return writer.destination.Write(chunk)
}
