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
            _leftLevel   = 0;
            _rightLevel  = 0;
            _turretLevel = 0;
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

    // Set left and right track speeds.  Values in [-1..1].
    // Internally quantised to PWM_STEPS levels; output applied on next tick().
    void setLeftRight(float l, float r)
    {
        _leftLevel  = _toLevel(l);
        _rightLevel = _toLevel(r);
    }

    // Set turret rotation speed.  Value in [-1..1].
    void setTurret(float v)
    {
        _turretLevel = _toLevel(v);
    }

    // Control the hull board test LED (IO1_7).
    void setHullLed(bool on)
    {
        if (on) _port1 |= BIT_HULL_LED;
        else    _port1 &= ~BIT_HULL_LED;
        _writeReg(PCA_REG_OUT1, _port1);
    }

    // Advance the software PWM counter.  Call once per millisecond from loop().
    // Builds the Port 1 byte and writes it only when the value changes,
    // minimising I2C traffic during steady-state (e.g. full speed or stopped).
    void tick()
    {
        _pwmCounter = (_pwmCounter + 1) % PWM_STEPS;

        if (!_enabled) return; // nothing to do; port already zeroed by enable(false)

        // Assemble motor bits into a fresh value, keeping SLEEP and hull LED intact
        uint8_t bits = _port1 & (BIT_SLEEP | BIT_HULL_LED);
        // Left track and turret are physically swapped on this board revision —
        // left drive signal goes to the turret connector and vice versa.
        bits |= _motorBits(_leftLevel,   BIT_T_IN1, BIT_T_IN2);
        bits |= _motorBits(_rightLevel,  BIT_R_IN1, BIT_R_IN2);
        bits |= _motorBits(_turretLevel, BIT_L_IN1, BIT_L_IN2);

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
    // Quantise float [-1..1] to signed integer [-PWM_STEPS..+PWM_STEPS].
    static int8_t _toLevel(float v)
    {
        v = fmaxf(-1.0f, fminf(1.0f, v));
        return (int8_t)roundf(v * PWM_STEPS);
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
    int8_t   _leftLevel   = 0;    // -PWM_STEPS..+PWM_STEPS
    int8_t   _rightLevel  = 0;
    int8_t   _turretLevel = 0;
    uint8_t  _pwmCounter  = 0;    // 0..(PWM_STEPS-1)
    uint8_t  _port1       = 0x00; // shadow of PCA9555 Port 1 output register
};
