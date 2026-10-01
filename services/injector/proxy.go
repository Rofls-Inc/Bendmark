package main

import (
	"errors"
	"io"
	"log"
	"net"
	"sync"
	"time"
)

func serve(listener net.Listener, upstreamAddress string, delay time.Duration) error {
	for {
		client, err := listener.Accept()
		if errors.Is(err, net.ErrClosed) {
			return nil
		}
		if err != nil {
			return err
		}
		go forwardConnection(client, upstreamAddress, delay)
	}
}

func forwardConnection(client net.Conn, upstreamAddress string, delay time.Duration) {
	defer client.Close()

	upstream, err := net.DialTimeout("tcp", upstreamAddress, connectTimeout)
	if err != nil {
		log.Printf("connect to %s: %v", upstreamAddress, err)
		return
	}
	defer upstream.Close()

	var copies sync.WaitGroup
	copies.Add(2)
	go func() {
		defer copies.Done()
		io.Copy(upstream, client)
		closeWrite(upstream)
	}()
	go func() {
		defer copies.Done()
		io.Copy(delayedWriter{destination: client, delay: delay}, upstream)
		closeWrite(client)
	}()
	copies.Wait()
}

func closeWrite(connection net.Conn) {
	if tcp, ok := connection.(*net.TCPConn); ok {
		tcp.CloseWrite()
	}
}

type delayedWriter struct {
	destination io.Writer
	delay       time.Duration
}

func (writer delayedWriter) Write(chunk []byte) (int, error) {
	if writer.delay > 0 {
		time.Sleep(writer.delay)
	}
	return writer.destination.Write(chunk)
}
