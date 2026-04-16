// IrController.h — IR transmit and directional receive.
//
// TX: 38 kHz carrier on GPIO39 (left barrel) and GPIO40 (right barrel) via LEDC.
//     Both fire simultaneously; the carrier is what TSOP receivers detect.
//
// RX: 8-direction TSOP receivers on PCA9555 Port 0, read via I2C.
//     Bits are active-LOW (0 = IR detected).  IrController reads Port 0 through
//     a pointer to MotorController_PCA9555 so both share the single I2C bus.
//
// Sequence (matches Unity ShootingController protocol):
//   1. Unity sends ir_emit_prepare  → startEmit()  + reply ir_emit_ready
//   2. Unity sends ir_listen_and_report ms:N → startListen(N)
//   3. update() called each loop tick; accumulates hits, closes window at t+N ms
//   4. isListenDone() → takeResult() → send ir_result {hit, dir} to Unity
//   5. Unity sends ir_emit_stop → stopEmit()
//
// Direction priority on multiple simultaneous hits: rear-facing receivers are
// reported first (S > SW > SE > others) because they carry the highest damage
// multiplier in game logic and are the most "interesting" result.

#pragma once
#include <Arduino.h>
#include "MotorController_PCA9555.h"

// ---- IR LED pins (turret board) ----
static constexpr int     IR_TX1_PIN    = 39;    // left barrel IR LED
static constexpr int     IR_TX2_PIN    = 40;    // right barrel IR LED

// ---- LEDC channels for IR carrier (avoid camera's channel 0) ----
// Camera XCLK uses LEDC_CHANNEL_0 / LEDC_TIMER_0.
// IR TX uses channels 4 & 5 on TIMER_1.
static constexpr uint8_t IR_LEDC_CH1   = 4;
static constexpr uint8_t IR_LEDC_CH2   = 5;
static constexpr uint32_t IR_FREQ_HZ   = 38000; // TSOP carrier frequency
static constexpr uint8_t IR_LEDC_BITS  = 8;     // 50% duty = 128/256

// PCA9555 Port 0 bit index → compass string
// Matches the physical layout on the hull board (bit 0 = N, clockwise).
static const char* const IR_DIR_NAMES[8] = {
    "N", "NE", "E", "SE", "S", "SW", "W", "NW"
};

struct IrResult {
    bool   hit;  // true if any receiver fired during the listen window
    String dir;  // compass direction of the most significant hit
};

class IrController
{
public:
    // Store reference to motor controller for shared PCA9555 access.
    void begin(MotorController_PCA9555& pca)
    {
        _pca = &pca;
        pinMode(IR_TX1_PIN, OUTPUT);
        digitalWrite(IR_TX1_PIN, LOW);
        pinMode(IR_TX2_PIN, OUTPUT);
        digitalWrite(IR_TX2_PIN, LOW);
        Serial.println("[IR] ready (TX GPIO39+40, RX PCA9555 port0)");
    }

    // Start 38 kHz carrier on both IR LED pins simultaneously.
    // Explicit channel assignment avoids conflicting with camera's LEDC channel 0.
    void startEmit()
    {
        if (_emitting) return;
        // ledcSetup + ledcAttachPin: compatible with both Arduino ESP32 2.x and 3.x
        ledcSetup(IR_LEDC_CH1, IR_FREQ_HZ, IR_LEDC_BITS);
        ledcSetup(IR_LEDC_CH2, IR_FREQ_HZ, IR_LEDC_BITS);
        ledcAttachPin(IR_TX1_PIN, IR_LEDC_CH1);
        ledcAttachPin(IR_TX2_PIN, IR_LEDC_CH2);
        uint32_t duty = (1u << IR_LEDC_BITS) / 2; // 50%
        ledcWrite(IR_LEDC_CH1, duty);
        ledcWrite(IR_LEDC_CH2, duty);
        _emitting = true;
        Serial.println("[IR] emit ON (38 kHz)");
    }

    // Stop carrier; leave pins LOW so LEDs are off.
    void stopEmit()
    {
        if (!_emitting) return;
        ledcDetachPin(IR_TX1_PIN);
        ledcDetachPin(IR_TX2_PIN);
        pinMode(IR_TX1_PIN, OUTPUT); digitalWrite(IR_TX1_PIN, LOW);
        pinMode(IR_TX2_PIN, OUTPUT); digitalWrite(IR_TX2_PIN, LOW);
        _emitting = false;
        Serial.println("[IR] emit OFF");
    }

    bool isEmitting() const { return _emitting; }

    // Open a hit-detection window of windowMs milliseconds.
    // Clears the accumulated hit mask from any previous window.
    void startListen(uint32_t windowMs)
    {
        _hitMask     = 0;
        _listening   = true;
        _resultReady = false;
        _listenEnd   = millis() + windowMs;
        Serial.printf("[IR] listen %u ms\n", windowMs);
    }

    // Call every loop tick.  While a window is open, reads PCA9555 Port 0
    // and ORs any triggered receivers into _hitMask.  Closes the window
    // and sets _resultReady when time expires.
    void update(uint32_t now)
    {
        if (!_listening) return;

        // Port 0 is active-LOW; invert so 1 = hit.
        uint8_t port0 = _pca->readPort0();
        _hitMask |= (~port0 & 0xFF);

        if ((int32_t)(now - _listenEnd) >= 0) {
            _listening   = false;
            _resultReady = true;
            Serial.printf("[IR] window closed, hitMask=0x%02X\n", _hitMask);
        }
    }

    // True once the listen window has expired and a result is ready to send.
    bool isListenDone() const { return _resultReady; }

    // Consume the result (clears isListenDone).  Always call this once
    // isListenDone() is true, even if the socket is closed, to reset state.
    //
    // Direction priority: S > SW > SE > others (rear-facing = 3× damage in
    // game logic, so reporting the rear direction is the most useful outcome
    // when multiple receivers fire at once).
    IrResult takeResult()
    {
        _resultReady = false;

        if (_hitMask == 0) {
            return { false, "" };
        }

        // Bit indices: N=0, NE=1, E=2, SE=3, S=4, SW=5, W=6, NW=7
        String dir;
        if      (_hitMask & (1u << 4)) dir = "S";  // highest priority
        else if (_hitMask & (1u << 5)) dir = "SW";
        else if (_hitMask & (1u << 3)) dir = "SE";
        else {
            for (int i = 0; i < 8; i++) {
                if (_hitMask & (1u << i)) { dir = IR_DIR_NAMES[i]; break; }
            }
        }

        return { true, dir };
    }

private:
    MotorController_PCA9555* _pca         = nullptr;
    bool     _emitting    = false;
    bool     _listening   = false;
    bool     _resultReady = false;
    uint32_t _listenEnd   = 0;
    uint8_t  _hitMask     = 0;  // accumulated receivers that fired (inverted Port 0)
};
