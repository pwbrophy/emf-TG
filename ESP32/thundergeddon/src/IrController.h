// IrController.h — IR transmit and directional receive for handshake mode.
//
// TX: 38 kHz carrier on GPIO39 (left barrel) and GPIO40 (right barrel) via LEDC ch4+5.
//     Burst sequence driven by beginEmitLeft/beginEmitRight + updateEmitBurst().
//     3ms on / 3ms off cycling prevents TSOP AGC saturation.
//
// RX: 8-direction TSOP receivers on PCA9555 Port 0, read via I2C (active-LOW).
//     Timed listen window driven by beginListenWindow() + updateWindow().
//     Early exit on first detection; misses wait out the full window.

#pragma once
#include <Arduino.h>
#include "MotorController_PCA9555.h"

// Declared in main.cpp; written by PCA9555 INT ISR (GPIO14 FALLING).
// IrController::updateWindow() consumes this flag to gate Port 0 reads.
extern volatile bool g_pca9555IntFired;

// ---- IR LED pins (turret board) ----
static constexpr int     IR_TX1_PIN    = 39;
static constexpr int     IR_TX2_PIN    = 40;
static constexpr uint8_t IR_LEDC_CH1   = 4;
static constexpr uint8_t IR_LEDC_CH2   = 5;
static constexpr uint32_t IR_FREQ_HZ   = 38000;
static constexpr uint8_t IR_LEDC_BITS  = 8;    // 50% duty = 128/256

// Handshake burst timing. Short 3ms on / 3ms off keeps the TSOP AGC from
// saturating while maximising pulse density across a short listen window.
static constexpr int HS_BURST_ON_MS  = 3;
static constexpr int HS_BURST_OFF_MS = 3;

// PCA9555 Port 0 bit index → compass string (bit 0=N, clockwise)
static const char* const IR_DIR_NAMES[8] = {
    "N", "NE", "E", "SE", "S", "SW", "W", "NW"
};

class IrController
{
public:
    void begin(MotorController_PCA9555& pca)
    {
        _pca = &pca;
        pinMode(IR_TX1_PIN, OUTPUT); digitalWrite(IR_TX1_PIN, LOW);
        pinMode(IR_TX2_PIN, OUTPUT); digitalWrite(IR_TX2_PIN, LOW);

        // PCA9555 INT (GPIO14): attachInterrupt is called from main.cpp setup()
        // after begin() returns, keeping the ISR and its literal pool in the same TU.
        _pca->readPort0(); // flush any pre-existing INT assertion before first slot

        Serial.println("[IR] ready (TX GPIO39+40, RX PCA9555 port0, INT GPIO14)");
    }

    // =========================================================
    // Handshake emit API (ACK-driven, no clock sync)
    // =========================================================

    // Fire left barrel (GPIO39) using 3ms on / 3ms off burst pattern.
    // Repeats autonomously via updateEmitBurst(). ACK sent by caller in main.cpp.
    void beginEmitLeft()
    {
        _setupLedc();
        _burstLed   = 1;
        _burstEnd   = millis() + (uint32_t)HS_BURST_ON_MS;
        _burstPhase = BurstPhase::On;
        _led1On();
        _emitting   = true;
    }

    // Switch to right barrel (GPIO40). LEDC already set up by beginEmitLeft.
    // Inherits the running burst pattern — ACK sent by caller immediately.
    void beginEmitRight()
    {
        _burstLed   = 2;
        _burstEnd   = millis() + (uint32_t)HS_BURST_ON_MS;
        _burstPhase = BurstPhase::On;
        _led2On();
    }

    // Drive the burst state machine. Call every loop tick while emitting.
    void updateEmitBurst(uint32_t now)
    {
        if (_burstPhase == BurstPhase::Idle) return;
        if ((int32_t)(now - _burstEnd) < 0) return;
        if (_burstPhase == BurstPhase::On) {
            _ledOff();
            _burstEnd   = now + (uint32_t)HS_BURST_OFF_MS;
            _burstPhase = BurstPhase::Off;
        } else {
            if (_burstLed == 1) _led1On(); else _led2On();
            _burstEnd   = now + (uint32_t)HS_BURST_ON_MS;
            _burstPhase = BurstPhase::On;
        }
    }

    // Stop all IR emission (called on ir_emit_stop or WebSocket close).
    void stopEmit()
    {
        _forceLedsOff();
        _emitting   = false;
        _burstPhase = BurstPhase::Idle;
    }

    bool isEmitting() const { return _emitting; }

    // =========================================================
    // Handshake listen window API
    // =========================================================

    // Start a timed listen window. Call updateWindow() each loop tick.
    // When isWindowDone() is true, call takeWindowMask() for the 8-bit hit mask.
    void beginListenWindow(uint32_t durationMs)
    {
        _pca->readPort0();        // flush any pre-existing INT
        g_pca9555IntFired = false;
        _winMask = 0;
        _winEnd  = millis() + durationMs;
        _inWin   = true;
        _winDone = false;
    }

    void updateWindow(uint32_t now)
    {
        if (!_inWin) return;
        if (g_pca9555IntFired) {
            g_pca9555IntFired = false;
            _winMask |= (~_pca->readPort0() & 0xFF);
            // Early exit on first detection — no point waiting for _winEnd
            if (_winMask != 0) {
                _inWin   = false;
                _winDone = true;
                _pca->readPort0(); // flush INT line
                Serial.printf("[IR] window early-exit mask=0x%02X\n", _winMask);
                return;
            }
        }
        if ((int32_t)(now - _winEnd) >= 0) {
            _inWin   = false;
            _winDone = true;
            _pca->readPort0(); // flush INT after window
            Serial.printf("[IR] window timeout mask=0x%02X\n", _winMask);
        }
    }

    bool    isWindowDone()  const { return _winDone; }
    uint8_t takeWindowMask()      { _winDone = false; return _winMask; }

private:
    MotorController_PCA9555* _pca = nullptr;

    // ---- Handshake window state ----
    bool     _inWin   = false;
    bool     _winDone = false;
    uint32_t _winEnd  = 0;
    uint8_t  _winMask = 0;

    // ---- Handshake burst state ----
    enum class BurstPhase : uint8_t { Idle, On, Off };
    BurstPhase _burstPhase = BurstPhase::Idle;
    uint32_t   _burstEnd   = 0;
    uint8_t    _burstLed   = 0; // 1 = left (GPIO39), 2 = right (GPIO40)

    bool _emitting = false;

    // ---- Helpers ----
    void _led1On()  { ledcWrite(IR_LEDC_CH1, 128); ledcWrite(IR_LEDC_CH2, 0);   }
    void _led2On()  { ledcWrite(IR_LEDC_CH1, 0);   ledcWrite(IR_LEDC_CH2, 128); }
    void _ledOff()  { ledcWrite(IR_LEDC_CH1, 0);   ledcWrite(IR_LEDC_CH2, 0);   }

    void _setupLedc()
    {
        ledcSetup(IR_LEDC_CH1, IR_FREQ_HZ, IR_LEDC_BITS);
        ledcSetup(IR_LEDC_CH2, IR_FREQ_HZ, IR_LEDC_BITS);
        ledcAttachPin(IR_TX1_PIN, IR_LEDC_CH1);
        ledcAttachPin(IR_TX2_PIN, IR_LEDC_CH2);
        ledcWrite(IR_LEDC_CH1, 0);
        ledcWrite(IR_LEDC_CH2, 0);
    }

    void _forceLedsOff()
    {
        ledcDetachPin(IR_TX1_PIN);
        ledcDetachPin(IR_TX2_PIN);
        pinMode(IR_TX1_PIN, OUTPUT); digitalWrite(IR_TX1_PIN, LOW);
        pinMode(IR_TX2_PIN, OUTPUT); digitalWrite(IR_TX2_PIN, LOW);
    }
};
