package main

// The players' address (-player-addr, set by the router's init script:
// 192.168.9.1): the hub's second address, outside the phones' own Wi-Fi range,
// for players' pages.
//
// Android 17 blocks apps, browsers included, from reaching the range the
// phone's Wi-Fi address is in (192.168.8.x here) until the player allows that
// browser "Nearby devices" (15 Nov 2026: a player couldn't load the page until
// they did). It blocks only that range: the on-link prefix of each of the
// phone's own addresses, plus multicast and broadcast
// (ConnectivityService.getEffectiveLocalPrefixes and netd.c's
// is_local_net_access_allowed, tag android-17.0.0_r1). Other private addresses,
// reached through the gateway, aren't blocked. The hub is every phone's
// gateway, so the same hub at an address outside that range loads with no
// permission.
//
// The address goes on the LAN bridge as a /32 (no subnet, no route), so
// nothing else changes: the tanks, DHCP and the router's own pages stay on
// 192.168.8.1. A restart of the router's network rebuilds the bridge without
// it, so it is checked every minute and put back.

import (
	"errors"
	"fmt"
	"log"
	"net/netip"
	"os/exec"
	"strings"
	"sync/atomic"
	"time"
)

const playerAddrEvery = time.Minute

type playerAddress struct {
	addr  netip.Addr
	iface string
	port  int
	up    atomic.Bool // on the bridge (as far as the last check knows)
	run   func(args ...string) ([]byte, error)
}

// players is the hub's players' address; nil = none (not on the router, or
// not asked for). Read by infoNow and the host's snapshot.
var players *playerAddress

// startPlayerAddr returns nil if addr is empty.
func startPlayerAddr(addr, iface string, port int) *playerAddress {
	if addr == "" {
		return nil
	}
	a, err := netip.ParseAddr(addr)
	if err != nil || !a.Is4() {
		log.Printf("[Network] players' address %q isn't an IPv4 address: not used", addr)
		return nil
	}
	p := &playerAddress{addr: a, iface: iface, port: port, run: runIP}
	go p.loop(time.NewTicker(playerAddrEvery).C)
	return p
}

func runIP(args ...string) ([]byte, error) {
	return exec.Command("ip", args...).CombinedOutput()
}

// url is the players' link base, "http://192.168.9.1:8088/", or "" while the
// address isn't on the bridge (safe on nil).
func (p *playerAddress) url() string {
	if p == nil || !p.up.Load() {
		return ""
	}
	return fmt.Sprintf("http://%s:%d/", p.addr, p.port)
}

func (p *playerAddress) loop(tick <-chan time.Time) {
	warned := false
	for {
		err := p.ensure()
		switch {
		case errors.Is(err, exec.ErrNotFound):
			log.Printf("[Network] no players' address: the hub isn't running on the router")
			return
		case errors.Is(err, errInsideLan):
			log.Printf("[Network] no players' address: %v", err)
			return
		case err != nil:
			if !warned {
				log.Printf("[Network] can't add the players' address yet: %v", err)
				warned = true
			}
		default:
			warned = false
		}
		<-tick
	}
}

var errInsideLan = errors.New("it is inside the LAN's own range, where Android 17 blocks it: choose one outside it")

// ensure puts the address on the bridge if it isn't there.
func (p *playerAddress) ensure() error {
	out, err := p.run("-4", "-o", "addr", "show", "dev", p.iface)
	if err != nil {
		p.up.Store(false)
		if errors.Is(err, exec.ErrNotFound) {
			return err
		}
		return fmt.Errorf("ip addr show %s: %v %s", p.iface, err, strings.TrimSpace(string(out)))
	}
	have, lan := false, netip.Prefix{}
	for _, f := range strings.Fields(string(out)) {
		pfx, err := netip.ParsePrefix(f)
		if err != nil || !pfx.Addr().Is4() {
			continue
		}
		switch {
		case pfx.Addr() == p.addr && pfx.Bits() == 32:
			have = true
		case pfx.Bits() < 32 && pfx.Contains(p.addr):
			lan = pfx
		}
	}
	if lan.IsValid() {
		p.up.Store(false)
		return fmt.Errorf("%s on %s (%s): %w", p.addr, p.iface, lan, errInsideLan)
	}
	if have {
		p.up.Store(true)
		return nil
	}
	if out, err := p.run("addr", "add", p.addr.String()+"/32", "dev", p.iface); err != nil {
		p.up.Store(false)
		return fmt.Errorf("ip addr add %s/32 dev %s: %v %s", p.addr, p.iface, err, strings.TrimSpace(string(out)))
	}
	log.Printf("[Network] players' address %s added to %s: players' link %s", p.addr, p.iface, fmt.Sprintf("http://%s:%d/", p.addr, p.port))
	p.up.Store(true)
	return nil
}
