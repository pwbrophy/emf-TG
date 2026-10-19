package main

// Turret stabilisation: with it on, the turret keeps pointing the same way
// while the hull turns, by turning against the hull at the hull's turn rate.
//
// The tanks have no compass or gyro, so this is open loop. The hub works out
// how fast the hull is turning from the drive commands it relays: it copies
// the firmware's acceleration ramp and its drive watchdog, then adds the
// tracks' spin-up and spin-down inertia (Tuning.StabSpinUpMs / StabSpinDownMs),
// and sends the turret the opposite speed. The player's own turret speed (the
// slider's slow and fast zones) is added on top.
//
// Corrections are kept small (Tuning.StabMax, normally the turret's lowest
// steady speed), so the turret runs in its pulsed mode, which moves it far
// more evenly at low speed than continuous low power does.
//
// The player switches it on and off with the STAB button on the in-game page
// ("stabilise" message); on a 2-player tank that is the gunner. The host
// tunes it in Advanced → Stabiliser.

import (
	"fmt"
	"math"
	"time"
)

const (
	stabTickEvery = 20 * time.Millisecond
	driveWatchdog = 500 * time.Millisecond // the firmware's DRIVE_WATCHDOG_MS: no drive command for this long = stop
)

// stabState is one tank's stabiliser in the running game.
type stabState struct {
	driveL, driveR float64   // the last drive command sent to the tank
	driveAt        time.Time // when it was sent
	turretIn       float64   // the player's own turret speed

	rampL, rampR float64 // the tracks' speeds after the firmware's ramp
	yaw          float64 // estimated hull turn rate, as a turret speed (+ = turning right)

	active bool      // the stabiliser is driving the turret
	sent   float64   // the last turret speed it sent
	sentAt time.Time // when
}

// stabWanted: the player who turns this tank's turret has stabilisation on
// (and the host allows it).
func (g *game) stabWanted(c *combatant) bool {
	if !g.settings.Tuning.StabAllowed {
		return false
	}
	p := g.players[c.shooter().device]
	return p != nil && p.stabilise
}

func (g *game) setStabilise(device string, on bool) {
	if p := g.players[device]; p != nil {
		p.stabilise = on
	}
	if c := g.seat(device); c != nil && c.shooter().device == device {
		g.stepStabiliser(c, time.Now(), 0) // on or off at once, not at the next tick
	}
}

// runStabiliser updates every stabilised turret 50 times a second.
func (g *game) runStabiliser(stop <-chan struct{}) {
	t := time.NewTicker(stabTickEvery)
	defer t.Stop()
	last := time.Now()
	for {
		select {
		case <-stop:
			return
		case now := <-t.C:
			g.tickStabiliser(now, now.Sub(last))
			last = now
		}
	}
}

func (g *game) tickStabiliser(now time.Time, dt time.Duration) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.phase != phasePlaying || g.match == nil {
		return
	}
	for _, c := range g.match.fighters {
		g.stepStabiliser(c, now, dt)
	}
}

// stepStabiliser advances one tank's hull model by dt and sends its turret
// the stabilised speed when that has changed. dt = 0 just re-sends at once.
func (g *game) stepStabiliser(c *combatant, now time.Time, dt time.Duration) {
	s := &c.stab
	if g.paused || c.life == exploding {
		// Motors are off: the hull isn't turning. Start again from rest.
		s.rampL, s.rampR, s.yaw, s.active = 0, 0, 0, false
		return
	}
	if !g.stabWanted(c) {
		if s.active {
			// Switched off: hand the turret back to the player's slider.
			s.active = false
			s.rampL, s.rampR, s.yaw = 0, 0, 0
			g.sendTurretSpeed(c, s.turretIn)
		}
		return
	}
	t := g.settings.Tuning

	// The tracks, as the firmware drives them.
	l, r := s.driveL, s.driveR
	if now.Sub(s.driveAt) > driveWatchdog {
		l, r = 0, 0
	}
	s.rampL = rampToward(s.rampL, l, t.DriveAccel, t.DriveDecel, dt)
	s.rampR = rampToward(s.rampR, r, t.DriveAccel, t.DriveDecel, dt)

	// The hull's turn rate, with its inertia: quicker or slower to get going
	// than to stop.
	target := t.stabTurnRate(s.rampL, s.rampR)
	tau := t.StabSpinDownMs
	if target*s.yaw >= 0 && math.Abs(target) > math.Abs(s.yaw) {
		tau = t.StabSpinUpMs
	}
	if tau <= 0 {
		s.yaw = target
	} else {
		s.yaw += (target - s.yaw) * (1 - math.Exp(-float64(dt)/float64(time.Duration(tau)*time.Millisecond)))
	}
	if math.Abs(s.yaw) < 0.002 && target == 0 {
		s.yaw = 0
	}

	// Counter-turn: a hull turning right needs the turret to turn left
	// (a positive turret speed, like the slider's left side).
	corr := max(-t.StabMax, min(s.yaw, t.StabMax))
	if t.StabInvert {
		corr = -corr
	}
	out := math.Round(clampUnit(s.turretIn+corr)*1000) / 1000

	first := !s.active
	s.active = true
	due := now.Sub(s.sentAt) >= time.Duration(t.StabSendMs)*time.Millisecond
	switch {
	case first, dt == 0:
	case out == s.sent:
		return
	case out == s.turretIn: // correction finished: settle at once
	case !due || math.Abs(out-s.sent) < 0.005:
		return
	}
	g.sendTurretSpeed(c, out)
	s.sent, s.sentAt = out, now
}

func (g *game) sendTurretSpeed(c *combatant, speed float64) {
	g.tanks.sendTo(c.tank, fmt.Sprintf(`{"cmd":"turret","speed":%.3f}`, speed))
}

// stabTurnRate is how fast the hull turns for these track speeds, as the
// turret speed that cancels it (before StabMax). + = turning right.
func (t Tuning) stabTurnRate(l, r float64) float64 {
	turn, fwd := (l-r)/2, (l+r)/2
	a := math.Abs(turn)
	if a <= t.StabDeadband || a == 0 {
		return 0
	}
	gain := t.StabGain
	if f := math.Abs(fwd); f > 0 {
		gain *= 1 + (t.StabArcGain-1)*f/(f+a) // turning while driving: blend toward the arc gain
	}
	rate := gain * math.Pow(a, t.StabCurve)
	if turn < 0 {
		rate = -rate
	}
	return rate
}

// rampToward moves cur toward tgt the way the firmware's _stepChannel does:
// accel / decel are seconds for the full 0→1 range (decel when the target is
// zero), 0 = instant.
func rampToward(cur, tgt, accel, decel float64, dt time.Duration) float64 {
	param := accel
	if tgt == 0 {
		param = decel
	}
	if param <= 0 {
		return tgt
	}
	step := dt.Seconds() / param
	if tgt > cur {
		return min(cur+step, tgt)
	}
	return max(cur-step, tgt)
}
