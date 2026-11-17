package main

import (
	"context"
	"errors"
)

var errPoolFull = errors.New("worker pool is full")

type workerPool struct {
	slots   chan struct{}
	waiting chan struct{}
}

// acquire reports whether the request had to join the queue.
// The caller releases the slot only after a successful acquire.
func (p workerPool) acquire(ctx context.Context) (bool, error) {
	select {
	case p.slots <- struct{}{}:
		return false, nil
	default:
	}

	select {
	case p.waiting <- struct{}{}:
		defer func() { <-p.waiting }()
	default:
		return false, errPoolFull
	}

	select {
	case p.slots <- struct{}{}:
		return true, nil
	case <-ctx.Done():
		return false, ctx.Err()
	}
}

func (p workerPool) release() {
	<-p.slots
}
