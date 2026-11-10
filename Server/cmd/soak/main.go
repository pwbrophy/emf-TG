// Soak test: bot players and a bot host play game after game against a hub
// running pretend tanks, to shake out anything that only goes wrong after
// many games in a row (leaks, players stuck between games, state left over
// from the game before).
//
// Never point it at a hub with real tanks connected: the bots drive.
//
//	# terminal 1: a hub with pretend tanks, its own settings and logs
//	go run . -addr 127.0.0.1:8099 -discovery-port 0 -fake-tanks 6 -wifi-iface "" \
//	    -settings soak/settings.json -log-dir soak -video-log soak/video.csv
//	# terminal 2
//	go run ./cmd/soak -server 127.0.0.1:8099 -players 6 -games 30
//
// Each game: everyone joins, picks an alliance, the host starts, the bots
// drive, turn and fire for -play seconds, the host ends the game, everyone
// taps Play Again. With -chaos some bots drop their connection mid-game and
// come back, as phones do. Afterwards read the hub's event log
// (/api/events, or logsummary.py on a fetched bundle): the "hub" lines'
// goroutines and go_heap_mb should be flat from the first game to the last.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"math/rand"
	"os"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"
)

type state struct {
	T              string `json:"t"`
	Phase          string `json:"phase"`
	Hosted         bool   `json:"hosted"`
	FireCooldownMs int    `json:"fireCooldownMs"`
	Alliances      []struct {
		ID string `json:"id"`
	} `json:"alliances"`
	Me struct {
		IsHost bool   `json:"isHost"`
		Status string `json:"status"`
		Tank   string `json:"tank"`
	} `json:"me"`
	Players []struct {
		Name   string `json:"name"`
		Status string `json:"status"`
		Tank   string `json:"tank"`
	} `json:"players"`
}

type bot struct {
	server, device, name string
	index                int

	mu   sync.Mutex
	conn *websocket.Conn
	st   atomic.Pointer[state]

	reconnects, errors, fireResults atomic.Int64
}

func (b *bot) send(v map[string]any) {
	b.mu.Lock()
	c := b.conn
	b.mu.Unlock()
	if c == nil {
		return
	}
	data, _ := json.Marshal(v)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	_ = c.Write(ctx, websocket.MessageText, data)
}

// drop closes the connection without a word, like a phone losing Wi-Fi.
func (b *bot) drop() {
	b.mu.Lock()
	if b.conn != nil {
		b.conn.CloseNow()
	}
	b.mu.Unlock()
}

// run keeps the bot connected, reading the hub's messages.
func (b *bot) run(ctx context.Context) {
	first := true
	for ctx.Err() == nil {
		c, _, err := websocket.Dial(ctx, "ws://"+b.server+"/ws", nil)
		if err != nil {
			time.Sleep(500 * time.Millisecond)
			continue
		}
		c.SetReadLimit(1 << 20)
		b.mu.Lock()
		b.conn = c
		b.mu.Unlock()
		if !first {
			b.reconnects.Add(1)
		}
		first = false
		b.send(map[string]any{"t": "hello", "device": b.device})
		for {
			_, data, err := c.Read(ctx)
			if err != nil {
				break
			}
			var head struct {
				T   string `json:"t"`
				Msg string `json:"msg"`
			}
			if json.Unmarshal(data, &head) != nil {
				continue
			}
			switch head.T {
			case "state":
				var s state
				if json.Unmarshal(data, &s) == nil {
					b.st.Store(&s)
				}
			case "error":
				b.errors.Add(1)
				log.Printf("%s was told: %s", b.name, head.Msg)
			case "fire_result":
				b.fireResults.Add(1)
			}
		}
		c.CloseNow()
		b.mu.Lock()
		b.conn = nil
		b.mu.Unlock()
		time.Sleep(300 * time.Millisecond)
	}
}

// play is a player: join, pick an alliance, drive and fire, play again.
func (b *bot) play(ctx context.Context) {
	tick := time.NewTicker(50 * time.Millisecond)
	defer tick.Stop()
	var lastSlow, lastFire, lastPing time.Time
	l, r, turret := 0.0, 0.0, 0.0
	for {
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
		}
		now := time.Now()
		if now.Sub(lastPing) > 5*time.Second {
			lastPing = now
			b.send(map[string]any{"t": "ping"})
		}
		s := b.st.Load()
		if s == nil {
			continue
		}
		slow := now.Sub(lastSlow) > time.Second
		switch s.Me.Status {
		case "":
			if slow {
				lastSlow = now
				b.send(map[string]any{"t": "join", "name": b.name})
			}
		case "lobby":
			if slow && s.Phase == "lobby" && s.Me.Tank == "" && len(s.Alliances) > 0 {
				lastSlow = now
				// Its own side, or the other one if that is full.
				a := s.Alliances[(b.index+rand.Intn(2)*rand.Intn(2))%len(s.Alliances)].ID
				b.send(map[string]any{"t": "pick_alliance", "alliance": a})
			}
		case "playing":
			if s.Phase != "playing" {
				continue
			}
			if slow {
				lastSlow = now
				l, r, turret = rand.Float64()*2-1, rand.Float64()*2-1, rand.Float64()*2-1
				b.send(map[string]any{"t": "turret", "s": turret})
			}
			b.send(map[string]any{"t": "drive", "l": l, "r": r})
			if cool := time.Duration(s.FireCooldownMs+50) * time.Millisecond; now.Sub(lastFire) > cool {
				lastFire = now
				b.send(map[string]any{"t": "fire"})
			}
		case "finished":
			if slow {
				lastSlow = now
				b.send(map[string]any{"t": "play_again"})
			}
		}
	}
}

func main() {
	server := flag.String("server", "127.0.0.1:8099", "the hub's address (one running -fake-tanks, never real tanks)")
	players := flag.Int("players", 6, "bot players (the hub needs at least this many pretend tanks)")
	games := flag.Int("games", 20, "games to play")
	play := flag.Duration("play", 8*time.Second, "how long each game is played before the host ends it")
	chaos := flag.Bool("chaos", true, "some bots drop their connection mid-game and reconnect")
	flag.Parse()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	run := fmt.Sprintf("%04x", rand.Intn(1<<16)) // so a second run's bots are new players
	host := &bot{server: *server, device: "soak-host-" + run, name: "Host"}
	go host.run(ctx)
	var bots []*bot
	for i := 0; i < *players; i++ {
		b := &bot{server: *server, device: fmt.Sprintf("soak-%s-%d", run, i), name: fmt.Sprintf("Bot %d", i+1), index: i}
		bots = append(bots, b)
		go b.run(ctx)
		go b.play(ctx)
	}

	started := time.Now()
	played := 0
	var phaseSince, lastAct time.Time
	lastPhase := ""
	stuck := 0
	dropped := false // this game's chance of a dropped phone has been taken
	for range time.Tick(200 * time.Millisecond) {
		s := host.st.Load()
		if s == nil {
			continue
		}
		now := time.Now()
		if s.Phase != lastPhase {
			lastPhase, phaseSince = s.Phase, now
			dropped = false
			if s.Phase == "ended" {
				played++
				log.Printf("game %d of %d done (%v so far)", played, *games, time.Since(started).Round(time.Second))
			}
		}
		act := now.Sub(lastAct) > time.Second
		inPhase := now.Sub(phaseSince)
		switch {
		case !s.Me.IsHost:
			if act {
				lastAct = now
				host.send(map[string]any{"t": "host_login"})
			}
		case s.Phase == "idle":
			if act {
				lastAct = now
				host.send(map[string]any{"t": "host_open"})
			}
		case s.Phase == "lobby":
			ready := 0
			for _, p := range s.Players {
				if p.Status == "lobby" && p.Tank != "" {
					ready++
				}
			}
			if ready == *players && act {
				lastAct = now
				host.send(map[string]any{"t": "host_start", "force": true})
			} else if inPhase > 30*time.Second && act {
				lastAct = now
				stuck++
				log.Printf("STUCK: only %d of %d players ready after %v in the lobby: %+v", ready, *players, inPhase.Round(time.Second), s.Players)
				if ready >= 2 {
					host.send(map[string]any{"t": "host_start", "force": true})
				}
			}
		case s.Phase == "playing":
			if *chaos && !dropped && inPhase > 2*time.Second && rand.Intn(2) == 0 {
				bots[rand.Intn(len(bots))].drop() // one phone loses its connection in about half the games
			}
			dropped = dropped || inPhase > 2*time.Second
			if inPhase > *play && act {
				lastAct = now
				host.send(map[string]any{"t": "host_end"})
			}
		case s.Phase == "ended":
			if inPhase > time.Second && act {
				lastAct = now
				if played >= *games {
					var rec, errs, fires int64
					for _, b := range bots {
						rec += b.reconnects.Load()
						errs += b.errors.Load()
						fires += b.fireResults.Load()
					}
					log.Printf("DONE: %d games in %v; %d shot results, %d reconnects, %d errors shown to players, %d times stuck in the lobby",
						played, time.Since(started).Round(time.Second), fires, rec, errs+host.errors.Load(), stuck)
					if stuck > 0 {
						os.Exit(1)
					}
					return
				}
				host.send(map[string]any{"t": "host_lobby"})
			}
		}
	}
}
