// MotorController_PCA9555.h
// Controls left/right tracks and turret via PCA9555 I/O expander over I2C.
// Also owns the I2C bus and exposes readPort0() for IrController to read
// the 8 directional IR receivers on Port 0.
//
// Variable speed is achieved via software PWM: tick() must be called from
// the main loop at ~1 ms intervals.  All motor bits are packed into a single
// Port 1 shadow byte and written in one I2C transaction per tick, giving
// PWM_STEPS-level resolution at 1000/PWM_STEPS Hz (default 125 Hz).
//
// Port 0 — inputs  (all 8 bits): IR receivers, active-LOW, 10 kΩ pull-ups.
// Port 1 — outputs (all 8 bits): motors + SLEEP + hull LED.
//
//  Bit  Signal          Note
//   0   Left  IN1
//   1   Left  IN2
//   2   Turret IN1
//   3   Turret IN2
//   4   MOTORS_SLEEP    HIGH = drivers awake (DRV8833 nSLEEP)
//   5   Right IN2       Note swapped vs IN1 per schematic
//   6   Right IN1
//   7   Hull LED

#pragma once
#include <Arduino.h>
#include <Wire.h>

// ---- PCA9555 register addresses ----
static constexpr uint8_t PCA_REG_IN0  = 0x00; // input  port 0 (read)
static constexpr uint8_t PCA_REG_OUT1 = 0x03; // output port 1 (write)
static constexpr uint8_t PCA_REG_CFG0 = 0x06; // config port 0 (1=input, 0=output)
static constexpr uint8_t PCA_REG_CFG1 = 0x07; // config port 1

// ---- Port 1 bit masks ----
static constexpr uint8_t BIT_L_IN1    = 0x01; // IO1_0
static constexpr uint8_t BIT_L_IN2    = 0x02; // IO1_1
static constexpr uint8_t BIT_T_IN1    = 0x04; // IO1_2
static constexpr uint8_t BIT_T_IN2    = 0x08; // IO1_3
static constexpr uint8_t BIT_SLEEP    = 0x10; // IO1_4
static constexpr uint8_t BIT_R_IN2    = 0x20; // IO1_5
static constexpr uint8_t BIT_R_IN1    = 0x40; // IO1_6
static constexpr uint8_t BIT_HULL_LED = 0x80; // IO1_7

// PWM resolution: 8 levels at 1 ms/tick = 125 Hz PWM.
// Raise to 16 for finer resolution (but 62 Hz may buzz some motors).
static constexpr uint8_t PWM_STEPS = 8;

// Low-speed tuning for one motor (or the pair of tracks).  The soft PWM only
// has PWM_STEPS levels and the motors stall below ~3/8 duty, so speeds under
// 'minSpeed' are produced by pulsing instead: each 'pulseMs' window is split
// into an on-part and a stopped part, so the motor moves in small even ticks
// at a lower average speed than it can hold continuously.
//   minSpeed     lowest speed that turns continuously (0 = never pulse)
//   pulseMs      length of one pulse window
//   kickMs       full-power burst at the start of every move / pulse (beats stiction; 0 = off)
//   backlashMs   extra full-power burst when a move goes the opposite way to
//                the previous one, to take up gear backlash (0 = off)
//   brake        brake (IN1=IN2=1) instead of coasting when stopped / between pulses
//   powerMatched how the on-part is sized:
//     false - on for |v|/minSpeed of the window (kick included).  With a kick
//             the pulses run at full power, so values just under minSpeed come
//             out faster than minSpeed.  Fine for the turret's fixed slow speed
//             (and what its play-tested settings were tuned against).
//     true  - on just long enough to deliver |v| x the window's full-power
//             energy, counting the kick, so speed rises smoothly with the
//             input right up to minSpeed.  Needed for an analog stick (drive).
struct MotorTuning
{
    float    minSpeed;
    uint16_t pulseMs;
    uint16_t kickMs;
    uint16_t backlashMs;
    bool     brake;
    bool     powerMatched;
};

class MotorController_PCA9555
{
public:
    // Start I2C and configure PCA9555.
    // sda/scl: ESP32 GPIO pins.  addr: PCA9555 I2C address (A0/A1/A2=GND → 0x20).
    bool begin(int sda, int scl, uint8_t addr = 0x20)
    {
        _addr = addr;
        Wire.begin(sda, scl);
        Wire.setClock(400000); // 400 kHz — enough for ~13k writes/s

        // Port 0: all inputs (IR receivers)
        _writeReg(PCA_REG_CFG0, 0xFF);
        // Port 1: all outputs (motors + LED)
        _writeReg(PCA_REG_CFG1, 0x00);
        // Safe initial state: SLEEP asserted (LOW), all INx low, LED off
        _port1 = 0x00;
        _writeReg(PCA_REG_OUT1, _port1);

        Serial.printf("[MOT] PCA9555 @ 0x%02X OK\n", _addr);
        return true;
    }

    // Enable or disable the DRV8833 drivers via MOTORS_SLEEP.
    // Disabling immediately coasts all motors and zeroes speed targets.
    void enable(bool on)
    {
        _enabled = on;
        if (on) {
            _port1 |= BIT_SLEEP;           // wake drivers
        } else {
            // Zero targets AND currents so the tank stops instantly on disable,
            // regardless of any in-progress inertia ramp.
            _left.reset();
            _right.reset();
            _turret.reset();
            // Clear all motor bits and assert SLEEP
            _port1 &= ~(BIT_L_IN1 | BIT_L_IN2 |
                        BIT_R_IN1 | BIT_R_IN2 |
                        BIT_T_IN1 | BIT_T_IN2 |
                        BIT_SLEEP);
        }
        _writeReg(PCA_REG_OUT1, _port1);
        Serial.printf("[MOT] enable(%s)\n", on ? "true" : "false");
    }

    bool isEnabled() const { return _enabled; }

    // True when all channels are at rest (targets and ramped currents at zero).
    // Used to pick a safe moment for blocking work like NVS flash writes.
    bool isIdle() const
    {
        return _left.idle() && _right.idle() && _turret.idle();
    }

    // Set acceleration/deceleration ramp times (seconds, 0 = instant).
    // driveAccel/driveDecel apply to both tracks; turretAccel/turretDecel to the turret.
    // "Accel" governs ramp-up when a non-zero target is commanded.
    // "Decel" governs coasting when the target returns to zero (stick released).
    void setPhysics(float driveAccel, float driveDecel, float turretAccel, float turretDecel)
    {
        _driveAccel  = driveAccel;
        _driveDecel  = driveDecel;
        _turretAccel = turretAccel;
        _turretDecel = turretDecel;
    }

    // Low-speed tuning (see MotorTuning).  powerMatched is fixed per channel.
    void setTurretTuning(float minSpeed, uint16_t pulseMs, uint16_t kickMs, bool brake,
                         uint16_t backlashMs)
    {
        _setTuning(_turretTuning, minSpeed, pulseMs, kickMs, brake, backlashMs);
    }

    void setDriveTuning(float minSpeed, uint16_t pulseMs, uint16_t kickMs, bool brake,
                        uint16_t backlashMs)
    {
        _setTuning(_driveTuning, minSpeed, pulseMs, kickMs, brake, backlashMs);
    }

    // Set left and right track speeds.  Values in [-1..1].
    // Stores as float targets; tick() ramps the current value toward the target.
    void setLeftRight(float l, float r)
    {
        _left.target  = fmaxf(-1.0f, fminf(1.0f, l));
        _right.target = fmaxf(-1.0f, fminf(1.0f, r));
    }

    // Set turret rotation speed.  Value in [-1..1].
    void setTurret(float v)
    {
        _turret.target = fmaxf(-1.0f, fminf(1.0f, v));
    }

    // Control the hull board test LED (IO1_7).
    void setHullLed(bool on)
    {
        if (on) _port1 |= BIT_HULL_LED;
        else    _port1 &= ~BIT_HULL_LED;
        _writeReg(PCA_REG_OUT1, _port1);
    }

    // Advance the software PWM counter.  Call once per millisecond from loop().
    // Ramps current speed values toward their targets using the configured physics
    // parameters, then builds the Port 1 byte and writes it only when it changes.
    void tick()
    {
        _pwmCounter = (_pwmCounter + 1) % PWM_STEPS;
        const uint32_t now = millis();

        // Ramp currents toward targets (runs even when disabled so state is clean).
        _stepChannel(_left.current,   _left.target,   _driveAccel,  _driveDecel);
        _stepChannel(_right.current,  _right.target,  _driveAccel,  _driveDecel);
        _stepChannel(_turret.current, _turret.target, _turretAccel, _turretDecel);
        // The tracks share one pulse window so they tick together; otherwise a
        // gentle curve (inner track pulsing, outer running) would wobble.
        _left.level   = _levelFor(_left,   _driveTuning,  now, &_right);
        _right.level  = _levelFor(_right,  _driveTuning,  now, &_left);
        _turret.level = _levelFor(_turret, _turretTuning, now, nullptr);

        if (!_enabled) return; // nothing to do; port already zeroed by enable(false)

        // Assemble motor bits into a fresh value, keeping SLEEP and hull LED intact
        uint8_t bits = _port1 & (BIT_SLEEP | BIT_HULL_LED);
        // Left track and turret are physically swapped on this board revision —
        // left drive signal goes to the turret connector and vice versa.
        bits |= _chanBits(_left.level,   _driveTuning.brake,  BIT_T_IN1, BIT_T_IN2);
        bits |= _chanBits(_right.level,  _driveTuning.brake,  BIT_R_IN1, BIT_R_IN2);
        bits |= _chanBits(_turret.level, _turretTuning.brake, BIT_L_IN1, BIT_L_IN2);

        if (bits != _port1) {
            _port1 = bits;
            _writeReg(PCA_REG_OUT1, _port1);
        }
    }

    // Read Port 0 (IR receivers).  Used by IrController.
    // Returns raw byte: bit N is LOW when receiver N sees IR (active-LOW).
    uint8_t readPort0()
    {
        Wire.beginTransmission(_addr);
        Wire.write(PCA_REG_IN0);
        Wire.endTransmission(false);            // repeated start (no STOP between write and read)
        Wire.requestFrom(_addr, (uint8_t)1);
        return Wire.available() ? Wire.read() : 0xFF; // 0xFF = no hits (safe default)
    }

private:
    // Per-motor state.
    struct Channel
    {
        float    target        = 0.0f;  // set by setLeftRight/setTurret
        float    current       = 0.0f;  // ramped toward target each tick
        int8_t   level         = 0;     // -PWM_STEPS..+PWM_STEPS for this tick
        int8_t   prevSign      = 0;     // sign of last tick's level (kick detection)
        int8_t   cmdSign       = 0;     // sign of the current command
        int8_t   lastDir       = 0;     // direction of the last move; kept across stops
        bool     pulsing       = false;
        uint32_t pulseStart    = 0;
        uint32_t kickUntil     = 0;
        uint32_t backlashUntil = 0;

        // lastDir survives: the gears stay where they were while disabled.
        void reset()
        {
            target = current = 0.0f;
            level = prevSign = cmdSign = 0;
            pulsing = false;
        }
        bool idle() const { return target == 0.0f && current == 0.0f; }
    };

    static void _setTuning(MotorTuning& t, float minSpeed, uint16_t pulseMs, uint16_t kickMs,
                           bool brake, uint16_t backlashMs)
    {
        t.minSpeed   = fmaxf(0.0f, fminf(1.0f, minSpeed));
        t.pulseMs    = pulseMs < 10 ? 10 : pulseMs;
        t.kickMs     = kickMs;
        t.brake      = brake;
        t.backlashMs = backlashMs;
    }

    // Quantise float [-1..1] to signed integer [-PWM_STEPS..+PWM_STEPS].
    static int8_t _toLevel(float v)
    {
        v = fmaxf(-1.0f, fminf(1.0f, v));
        return (int8_t)roundf(v * PWM_STEPS);
    }

    // PWM level for this tick, applying backlash, pulse mode and kick-start
    // (see MotorTuning).  Timed with millis() so a slow loop() iteration
    // doesn't stretch the pulses.  'partner' shares its pulse window (tracks).
    static int8_t _levelFor(Channel& c, const MotorTuning& t, uint32_t now, const Channel* partner)
    {
        const float v   = c.current;
        const float mag = fabsf(v);

        // Backlash: when the commanded direction is opposite to the last move
        // (remembered across stops - the gears stay where they were), run a
        // full-power burst of backlashMs to take up the slack.  It is driven
        // by the command, not the pulse, so pulse mode can't cut it short; the
        // pulse window restarts once the burst is done.  Releasing cancels it.
        const int8_t cmdSign = (v > 0) - (v < 0);
        if (cmdSign != c.cmdSign) {
            c.backlashUntil = now;
            if (cmdSign != 0) {
                if (c.lastDir != 0 && cmdSign != c.lastDir)
                    c.backlashUntil = now + t.backlashMs;
                c.lastDir = cmdSign;
                c.pulsing = false;
            }
            c.cmdSign = cmdSign;
        }
        if (cmdSign != 0 && (int32_t)(c.backlashUntil - now) > 0) {
            c.prevSign = cmdSign;   // already moving: no kick when the burst ends
            return cmdSign * (int8_t)PWM_STEPS;
        }

        int8_t level;
        if (mag == 0.0f) {
            level = 0;
            c.pulsing = false;
        } else if (mag < t.minSpeed) {
            if (!c.pulsing) {
                c.pulsing    = true;
                c.pulseStart = (partner && partner->pulsing) ? partner->pulseStart : now;
            }
            int8_t pulseLevel = _toLevel(t.minSpeed);
            if (pulseLevel < 1) pulseLevel = 1;
            uint32_t onMs;
            if (t.powerMatched) {
                float energy = mag * t.pulseMs;   // full-power ms needed this window
                if (energy <= t.kickMs) onMs = (uint32_t)lroundf(energy);
                else onMs = t.kickMs + (uint32_t)lroundf((energy - t.kickMs) * PWM_STEPS / pulseLevel);
            } else {
                onMs = (uint32_t)lroundf(t.pulseMs * mag / t.minSpeed);
            }
            if (onMs < 1) onMs = 1;
            if (onMs > t.pulseMs) onMs = t.pulseMs;
            uint32_t phase = (now - c.pulseStart) % t.pulseMs;
            level = (phase < onMs) ? (v > 0 ? pulseLevel : -pulseLevel) : 0;
        } else {
            level = _toLevel(v);
            c.pulsing = false;
        }

        // Kick-start: full power for the first kickMs of every move from rest
        // (including each pulse) or direction reversal.
        int8_t sign = (level > 0) - (level < 0);
        if (sign != 0 && sign != c.prevSign && t.kickMs > 0)
            c.kickUntil = now + t.kickMs;
        c.prevSign = sign;
        if (sign != 0 && (int32_t)(c.kickUntil - now) > 0)
            level = sign * (int8_t)PWM_STEPS;

        return level;
    }

    // Advance 'cur' one tick (1 ms) toward 'tgt' using the appropriate ramp rate.
    // target == 0 → use decel (stick released, coasting to stop).
    // target != 0 → use accel (player is actively steering).
    // param == 0  → instant (step of 2.0 covers the full [-1..1] range in one tick).
    static void _stepChannel(float& cur, float tgt, float accel, float decel)
    {
        if (cur == tgt) return;
        float param = (tgt == 0.0f) ? decel : accel;
        float step  = (param <= 0.0f) ? 2.0f : (1.0f / (param * 1000.0f));
        if (tgt > cur) cur = fminf(cur + step, tgt);
        else           cur = fmaxf(cur - step, tgt);
    }

    // INx bits for one channel: brake (IN1=IN2=1) when stopped if enabled,
    // otherwise the soft-PWM drive bits.
    uint8_t _chanBits(int8_t level, bool brake, uint8_t bitIN1, uint8_t bitIN2)
    {
        if (level == 0 && brake) return bitIN1 | bitIN2;   // brake: stop dead, no drift
        return _motorBits(level, bitIN1, bitIN2);
    }

    // Return the two INx output bits for one H-bridge at the current PWM phase.
    // level > 0 → forward (IN1=1, IN2=0)
    // level < 0 → reverse (IN1=0, IN2=1)
    // level == 0 → coast  (IN1=0, IN2=0)
    // During the OFF portion of the PWM cycle, also returns 0 (coast).
    uint8_t _motorBits(int8_t level, uint8_t bitIN1, uint8_t bitIN2)
    {
        if (level == 0) return 0;
        uint8_t mag = (uint8_t)(level < 0 ? -level : level);
        if (_pwmCounter >= mag) return 0;       // off phase → coast
        return (level > 0) ? bitIN1 : bitIN2;   // forward or reverse
    }

    // Write one byte to a PCA9555 register.
    void _writeReg(uint8_t reg, uint8_t val)
    {
        Wire.beginTransmission(_addr);
        Wire.write(reg);
        Wire.write(val);
        Wire.endTransmission();
    }

    uint8_t  _addr        = 0x20;
    bool     _enabled     = false;
    uint8_t  _pwmCounter  = 0;    // 0..(PWM_STEPS-1)
    uint8_t  _port1       = 0x00; // shadow of PCA9555 Port 1 output register

    Channel _left, _right, _turret;

    // Inertia parameters (seconds; 0 = instant).
    float _driveAccel  = 0.0f;
    float _driveDecel  = 0.0f;
    float _turretAccel = 0.0f;
    float _turretDecel = 0.0f;

    // Low-speed tuning.  Turret defaults are the play-tested values; drive
    // defaults are a starting point (pulse a little longer for the heavier hull).
    MotorTuning _turretTuning { 0.4f, 20, 20, 110, true, false };
    MotorTuning _driveTuning  { 0.4f, 40, 20,   0, true, true  };
};
