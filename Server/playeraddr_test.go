package main

import (
	"errors"
	"net/netip"
	"os/exec"
	"strings"
	"testing"
)

// fakeIP stands in for the ip command: show prints the bridge's addresses,
// add puts one there.
type fakeIP struct {
	addrs []string // "192.168.8.1/24"
	adds  int
	fail  error
}

func (f *fakeIP) run(args ...string) ([]byte, error) {
	if f.fail != nil {
		return nil, f.fail
	}
	switch strings.Join(args[:2], " ") {
	case "-4 -o":
		var b strings.Builder
		for _, a := range f.addrs {
			b.WriteString("5: br-lan    inet " + a + " scope global br-lan\\       valid_lft forever preferred_lft forever\n")
		}
		return []byte(b.String()), nil
	case "addr add":
		f.adds++
		f.addrs = append(f.addrs, args[2])
		return nil, nil
	}
	return nil, errors.New("unexpected ip " + strings.Join(args, " "))
}

func newTestPlayerAddr(addr string, ip *fakeIP) *playerAddress {
	return &playerAddress{addr: netip.MustParseAddr(addr), iface: "br-lan", port: 8088, run: ip.run}
}

func TestPlayerAddrAddsOnce(t *testing.T) {
	ip := &fakeIP{addrs: []string{"192.168.8.1/24"}}
	p := newTestPlayerAddr("192.168.9.1", ip)
	if p.url() != "" {
		t.Fatalf("url before the address is up = %q", p.url())
	}
	for i := 0; i < 3; i++ {
		if err := p.ensure(); err != nil {
			t.Fatal(err)
		}
	}
	if ip.adds != 1 {
		t.Fatalf("added %d times, want once", ip.adds)
	}
	if got := p.url(); got != "http://192.168.9.1:8088/" {
		t.Fatalf("url = %q", got)
	}

	// The router's network restarted: the bridge lost it. Put it back.
	ip.addrs = []string{"192.168.8.1/24"}
	if err := p.ensure(); err != nil || ip.adds != 2 || p.url() == "" {
		t.Fatalf("after losing it: err %v, adds %d, url %q", err, ip.adds, p.url())
	}
}

func TestPlayerAddrInsideLanRefused(t *testing.T) {
	ip := &fakeIP{addrs: []string{"192.168.8.1/16"}}
	p := newTestPlayerAddr("192.168.9.1", ip)
	if err := p.ensure(); !errors.Is(err, errInsideLan) {
		t.Fatalf("err = %v, want errInsideLan", err)
	}
	if ip.adds != 0 || p.url() != "" {
		t.Fatalf("adds %d, url %q: it shouldn't be used", ip.adds, p.url())
	}
}

func TestPlayerAddrNoIPCommand(t *testing.T) {
	p := newTestPlayerAddr("192.168.9.1", &fakeIP{fail: exec.ErrNotFound})
	if err := p.ensure(); !errors.Is(err, exec.ErrNotFound) || p.url() != "" {
		t.Fatalf("err %v, url %q", err, p.url())
	}
	var none *playerAddress
	if none.url() != "" {
		t.Fatal("nil url should be empty")
	}
}
