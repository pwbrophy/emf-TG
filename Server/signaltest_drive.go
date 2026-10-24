package main

// Signal Quality Test with driving (Settings.SignalTest.Drive): the tanks
// stand lifted, tracks off the ground, and the tracks run a game-like
// pattern while the turrets turn, so the drive motors make the noise they
// make in a game. The pattern looks random (driving fast and slow, arcs,
// spins, reversing, stops) but is made from a fixed seed: every tank runs
// the same commands at the same moment, and every run is the same, so runs
// can be compared.

import (
	"context"
	"fmt"
	"sort"
	"time"
)

// sigDrivePatternName goes into each run's settings, so runs made with a
// different pattern can be told apart if it ever changes. Change it with
// the seed or the moves below.
const (
	sigDrivePatternName = "game-1"
	sigDriveSeed        = 20261024
	sigDriveLength      = 180 * time.Second // then it starts again
	sigDriveStep        = 50 * time.Millisecond
)

// sigDriveMove: the joystick eases from where the last move left it to
// (x, y) over ramp, then holds until the move ends. As on the phone, the
// tracks are l = y+x and r = y-x.
type sigDriveMove struct {
	start, ramp, dur time.Duration
	x, y             float64
}

// sigMoveKinds: what a player does, and how often (weights).
var sigMoveKinds = []struct {
	weight float64
	pick   func(r *sigRand) (x, y, secs float64)
}{
	{25, func(r *sigRand) (float64, float64, float64) { return 0, 1, r.between(1, 3) }},                      // full speed ahead
	{10, func(r *sigRand) (float64, float64, float64) { return 0, r.between(0.3, 0.7), r.between(1, 2.5) }},  // creeping forward
	{12, func(r *sigRand) (float64, float64, float64) { return 0, -r.between(0.5, 1), r.between(0.5, 1.5) }}, // reversing
	{15, func(r *sigRand) (float64, float64, float64) { return r.sign(), 0, r.between(0.3, 1.2) }},           // spinning on the spot
	{18, func(r *sigRand) (float64, float64, float64) {
		return r.sign() * r.between(0.3, 0.7), r.between(0.6, 1), r.between(1, 2.5)
	}}, // turning while driving
	{20, func(r *sigRand) (float64, float64, float64) { return 0, 0, r.between(0.3, 2) }}, // stopped, aiming
}

var sigDrivePattern = makeDrivePattern(sigDriveSeed, sigDriveLength)

func makeDrivePattern(seed uint64, length time.Duration) []sigDriveMove {
	r := sigRand(seed)
	var total float64
	for _, k := range sigMoveKinds {
		total += k.weight
	}
	var moves []sigDriveMove
	at := time.Duration(0)
	for at < length {
		pick := r.float() * total
		k := sigMoveKinds[len(sigMoveKinds)-1]
		for _, c := range sigMoveKinds {
			if pick < c.weight {
				k = c
				break
			}
			pick -= c.weight
		}
		x, y, secs := k.pick(&r)
		dur := time.Duration(secs * float64(time.Second)).Round(sigDriveStep)
		ramp := min(time.Duration(r.between(0.15, 0.4)*float64(time.Second)).Round(sigDriveStep), dur)
		moves = append(moves, sigDriveMove{start: at, ramp: ramp, dur: dur, x: x, y: y})
		at += dur
	}
	return moves
}

// drivePatternAt: the tracks' speeds t into the pattern (it repeats).
func drivePatternAt(moves []sigDriveMove, t time.Duration) (l, r float64) {
	last := moves[len(moves)-1]
	t %= last.start + last.dur
	i := sort.Search(len(moves), func(i int) bool { return moves[i].start > t }) - 1
	m := moves[i]
	var px, py float64 // where the joystick was: the move before (stopped before the first)
	if i > 0 {
		px, py = moves[i-1].x, moves[i-1].y
	}
	f := 1.0
	if m.ramp > 0 && t-m.start < m.ramp {
		f = float64(t-m.start) / float64(m.ramp)
	}
	x, y := px+(m.x-px)*f, py+(m.y-py)*f
	return clampUnit(y + x), clampUnit(y - x)
}

// driveLoop runs the pattern on every tank at once until ctx ends, then
// stops the tracks.
func (st *signalTest) driveLoop(ctx context.Context, ids []string, done chan struct{}) {
	defer close(done)
	drive := func(l, r float64) {
		msg := fmt.Sprintf(`{"cmd":"drive","l":%.3f,"r":%.3f}`, l, r)
		for _, id := range ids {
			st.send(id, msg)
		}
	}
	start := time.Now()
	tick := time.NewTicker(sigDriveStep)
	defer tick.Stop()
	for {
		drive(drivePatternAt(sigDrivePattern, time.Since(start)))
		select {
		case <-ctx.Done():
			drive(0, 0)
			return
		case <-tick.C:
		}
	}
}

// sigRand is splitmix64: a tiny random number generator written out here
// so the pattern never changes with a Go upgrade.
type sigRand uint64

func (r *sigRand) next() uint64 {
	*r += 0x9E3779B97F4A7C15
	z := uint64(*r)
	z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
	z = (z ^ (z >> 27)) * 0x94D049BB133111EB
	return z ^ (z >> 31)
}

func (r *sigRand) float() float64 { return float64(r.next()>>11) / (1 << 53) }

func (r *sigRand) between(a, b float64) float64 { return a + (b-a)*r.float() }

func (r *sigRand) sign() float64 {
	if r.next()&1 == 0 {
		return -1
	}
	return 1
}
