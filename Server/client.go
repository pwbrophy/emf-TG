package main

// Phone pages talk to the game over one WebSocket each (GET /ws), sending
// small JSON messages ({"t":"join",...}) and receiving state snapshots.

import (
	"context"
	"encoding/json"
	"log"
	"net/http"
	"sync"
	"time"

	"github.com/coder/websocket"
)

// Pages send a keep-alive every 5 s (and drive input 20 times a second while
// playing), so this much silence means the page is gone even if its
// connection hasn't closed.
const clientSilenceTimeout = 15 * time.Second

type client struct {
	device string // set by the page's hello; guarded by game.mu

	out       chan []byte
	closeOnce sync.Once
	closed    chan struct{}
}

// push queues a message for the page without blocking. A page that can't
// keep up (its queue is full) is disconnected; it reconnects and gets a
// fresh snapshot.
func (c *client) push(b []byte) {
	select {
	case c.out <- b:
	default:
		c.close()
	}
}

func (c *client) close() {
	c.closeOnce.Do(func() { close(c.closed) })
}

func (g *game) handleWS(w http.ResponseWriter, r *http.Request) {
	conn, err := websocket.Accept(w, r, nil)
	if err != nil {
		return
	}
	conn.SetReadLimit(8192)
	defer conn.CloseNow()

	c := &client{out: make(chan []byte, 32), closed: make(chan struct{})}
	defer g.clientGone(c)

	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()

	// Writer: sends queued messages, and pings so dead connections are noticed.
	go func() {
		defer cancel()
		ping := time.NewTicker(5 * time.Second)
		defer ping.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-c.closed:
				return
			case b := <-c.out:
				wctx, wcancel := context.WithTimeout(ctx, 5*time.Second)
				err := conn.Write(wctx, websocket.MessageText, b)
				wcancel()
				if err != nil {
					return
				}
			case <-ping.C:
				pctx, pcancel := context.WithTimeout(ctx, 5*time.Second)
				err := conn.Ping(pctx)
				pcancel()
				if err != nil {
					return
				}
			}
		}
	}()

	for {
		rctx, rcancel := context.WithTimeout(ctx, clientSilenceTimeout)
		typ, data, err := conn.Read(rctx)
		rcancel()
		if err != nil {
			return
		}
		if typ != websocket.MessageText {
			continue
		}
		var m inMsg
		if err := json.Unmarshal(data, &m); err != nil {
			log.Printf("[Phones] bad message: %v", err)
			continue
		}
		if m.T == "ping" {
			continue // keep-alive from the page
		}
		g.handle(c, m)
	}
}
