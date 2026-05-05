// IrController.h — IR transmit (slot-scheduled bursts) and directional receive.
//
// TX: 38 kHz carrier on GPIO39 (left barrel) and GPIO40 (right barrel) via LEDC ch4+5.
//     Burst sequence driven by scheduleFireSlot() + updateFire().
//     LEDC channels are set up once per slot and torn down when the slot ends —
//     no repeated ledcSetup calls during bursts; duty is toggled with ledcWrite.
//
// RX: 8-direction TSOP receivers on PCA9555 Port 0, read via I2C (active-LOW).
//     Subwindow classification driven by scheduleListenSlot() + updateListen().
//
// Time model:
//   Unity sends time_sync {ut} → robot stores g_unityTimeOffset = ut - millis().
//   All slot times from Unity are Unity-ms; convert: localMs = unityMs - offset.
//
// All time comparisons use (int32_t)(now - target) >= 0 for millis() wraparound safety.

#pragma once
#include <Arduino.h>
#include "MotorController_PCA9555.h"

// ---- IR LED pins (turret board) ----
static constexpr int     IR_TX1_PIN    = 39;
static constexpr int     IR_TX2_PIN    = 40;
static constexpr uint8_t IR_LEDC_CH1   = 4;
static constexpr uint8_t IR_LEDC_CH2   = 5;
static constexpr uint32_t IR_FREQ_HZ   = 38000;
static constexpr uint8_t IR_LEDC_BITS  = 8;    // 50% duty = 128/256

// PCA9555 Port 0 bit index → compass string (bit 0=N, clockwise)
static const char* const IR_DIR_NAMES[8] = {
    "N", "NE", "E", "SE", "S", "SW", "W", "NW"
};

// ---- Legacy result (kept for ir_listen_and_report compatibility) ----
struct IrResult {
    bool   hit;
    String dir;
};

// ---- New slot result ----
// b1Mask / b2Mask: 8-bit bitmasks, bit N = 1 if that direction's receiver
// detected the burst on any repetition (ORed across reps).
struct IrSlotResult {
    int     slotId;
    uint8_t b1Mask;
    uint8_t b2Mask;
};

class IrController
{
public:
    void begin(MotorController_PCA9555& pca)
    {
        _pca = &pca;
        pinMode(IR_TX1_PIN, OUTPUT); digitalWrite(IR_TX1_PIN, LOW);
        pinMode(IR_TX2_PIN, OUTPUT); digitalWrite(IR_TX2_PIN, LOW);
        Serial.println("[IR] ready (TX GPIO39+40, RX PCA9555 port0)");
    }

    // =========================================================
    // Legacy emit API — kept so onWsClose() can call stopEmit()
    // =========================================================

    void startEmit()
    {
        if (_emitting) return;
        ledcSetup(IR_LEDC_CH1, IR_FREQ_HZ, IR_LEDC_BITS);
        ledcSetup(IR_LEDC_CH2, IR_FREQ_HZ, IR_LEDC_BITS);
        ledcAttachPin(IR_TX1_PIN, IR_LEDC_CH1);
        ledcAttachPin(IR_TX2_PIN, IR_LEDC_CH2);
        ledcWrite(IR_LEDC_CH1, 128);
        ledcWrite(IR_LEDC_CH2, 128);
        _emitting = true;
    }

    // Stops all IR emission; also resets the fire slot state machine so
    // onWsClose() safely aborts any in-progress slot.
    void stopEmit()
    {
        _forceLedsOff();
        _emitting   = false;
        _fireActive = false;
        _firePhase  = FirePhase::Idle;
    }

    // True while a legacy emit or a fire slot is active.
    bool isEmitting() const { return _emitting || _fireActive; }

    // =========================================================
    // Legacy listen API — kept for ir_listen_and_report
    // =========================================================

    void startListen(uint32_t windowMs)
    {
        _hitMask     = 0;
        _listening   = true;
        _resultReady = false;
        _listenEnd   = millis() + windowMs;
        Serial.printf("[IR] legacy listen %u ms\n", windowMs);
    }

    // Called every loop tick; accumulates hits, closes window at timeout.
    void update(uint32_t now)
    {
        if (!_listening) return;
        uint8_t port0 = _pca->readPort0();
        _hitMask |= (~port0 & 0xFF);
        if ((int32_t)(now - _listenEnd) >= 0) {
            _listening   = false;
            _resultReady = true;
            Serial.printf("[IR] legacy window closed hitMask=0x%02X\n", _hitMask);
        }
    }

    bool isListenDone() const { return _resultReady; }

    IrResult takeResult()
    {
        _resultReady = false;
        if (_hitMask == 0) return { false, "" };
        String dir;
        if      (_hitMask & (1u << 4)) dir = "S";
        else if (_hitMask & (1u << 5)) dir = "SW";
        else if (_hitMask & (1u << 3)) dir = "SE";
        else {
            for (int i = 0; i < 8; i++)
                if (_hitMask & (1u << i)) { dir = IR_DIR_NAMES[i]; break; }
        }
        return { true, dir };
    }

    // =========================================================
    // NEW: Fire slot (this robot is the shooter)
    // =========================================================
    // slotStartLocal: local millis() value at which burst1 of rep0 begins.
    // Timing layout per repetition:
    //   [0 .. b1Dur)              burst1 ON
    //   [b1Dur .. b1Dur+gap12)    pause (AGC reset)
    //   [b1Dur+gap12 .. +b2Dur)   burst2 ON
    //   [+b2Dur .. +repGap)       pause before next rep

    void scheduleFireSlot(int slotId, int32_t slotStartMs,
                          int b1Dur, int gap12, int b2Dur, int repGap, int reps)
    {
        _fireSlotId  = slotId;
        _fireStart   = (uint32_t)slotStartMs;
        _fb1Dur      = b1Dur;
        _fgap12      = gap12;
        _fb2Dur      = b2Dur;
        _frepGap     = repGap;
        _fReps       = reps;
        _fRepsDone   = 0;
        _firePhase   = FirePhase::WaitingForStart;
        _fireActive  = true;
        // Set up LEDC channels once for the whole slot; toggled via ledcWrite.
        ledcSetup(IR_LEDC_CH1, IR_FREQ_HZ, IR_LEDC_BITS);
        ledcSetup(IR_LEDC_CH2, IR_FREQ_HZ, IR_LEDC_BITS);
        ledcAttachPin(IR_TX1_PIN, IR_LEDC_CH1);
        ledcAttachPin(IR_TX2_PIN, IR_LEDC_CH2);
        ledcWrite(IR_LEDC_CH1, 0);
        ledcWrite(IR_LEDC_CH2, 0);
        Serial.printf("[IR] fire slot %d scheduled localStart=%u\n", slotId, _fireStart);
    }

    // Call every loop tick; drives burst on/off transitions autonomously.
    void updateFire(uint32_t now)
    {
        if (!_fireActive) return;

        switch (_firePhase)
        {
        case FirePhase::WaitingForStart:
            if ((int32_t)(now - _fireStart) >= 0) {
                _fRepsDone = 0;
                _fRepStart = _fireStart;
                _firePhase = FirePhase::Burst1;
                _ledOn();
                Serial.printf("[IR] fire slot %d rep0 burst1 ON\n", _fireSlotId);
            }
            break;

        case FirePhase::Burst1:
            if ((int32_t)(now - (_fRepStart + (uint32_t)_fb1Dur)) >= 0) {
                _ledOff();
                _firePhase = FirePhase::Gap12;
            }
            break;

        case FirePhase::Gap12:
            if ((int32_t)(now - (_fRepStart + (uint32_t)(_fb1Dur + _fgap12))) >= 0) {
                _ledOn();
                _firePhase = FirePhase::Burst2;
            }
            break;

        case FirePhase::Burst2:
            if ((int32_t)(now - (_fRepStart + (uint32_t)(_fb1Dur + _fgap12 + _fb2Dur))) >= 0) {
                _ledOff();
                _fRepsDone++;
                if (_fRepsDone >= _fReps) {
                    _endFireSlot();
                } else {
                    uint32_t perRep = (uint32_t)(_fb1Dur + _fgap12 + _fb2Dur + _frepGap);
                    _fRepStart += perRep;
                    _firePhase  = FirePhase::Burst1;
                    _ledOn();
                    Serial.printf("[IR] fire slot %d rep%d burst1 ON\n",
                                  _fireSlotId, _fRepsDone);
                }
            }
            break;

        default: break;
        }
    }

    // =========================================================
    // NEW: Listen slot (all non-shooting robots)
    // =========================================================
    // Classifies PCA9555 Port 0 reads into b1Mask vs b2Mask per burst subwindow.
    // ORs results across all repetitions so each direction's b1/b2 is true if
    // that burst was detected on ANY repetition.

    void scheduleListenSlot(int slotId, int32_t slotStartMs,
                            int b1Dur, int gap12, int b2Dur, int repGap, int reps)
    {
        _listenSlotId = slotId;
        _listenStart  = (uint32_t)slotStartMs;
        _lb1Dur       = b1Dur;
        _lgap12       = gap12;
        _lb2Dur       = b2Dur;
        _lrepGap      = repGap;
        _lReps        = reps;
        _lRepsDone    = 0;
        _lb1Mask      = 0;
        _lb2Mask      = 0;
        _listenPhase  = ListenPhase::WaitingForStart;
        _slotDone     = false;
        Serial.printf("[IR] listen slot %d scheduled localStart=%u\n", slotId, _listenStart);
    }

    // Call every loop tick; classifies detections into burst subwindows.
    void updateListen(uint32_t now)
    {
        if (_listenPhase == ListenPhase::Idle) return;

        uint32_t perRep = (uint32_t)(_lb1Dur + _lgap12 + _lb2Dur + _lrepGap);

        switch (_listenPhase)
        {
        case ListenPhase::WaitingForStart:
            if ((int32_t)(now - _listenStart) >= 0) {
                _lRepsDone = 0;
                _lRepStart = _listenStart;
                _listenPhase = ListenPhase::Burst1Window;
            }
            break;

        case ListenPhase::Burst1Window:
            {
                uint8_t port0 = _pca->readPort0();
                _lb1Mask |= (~port0 & 0xFF);
                if ((int32_t)(now - (_lRepStart + (uint32_t)_lb1Dur)) >= 0)
                    _listenPhase = ListenPhase::Gap12;
            }
            break;

        case ListenPhase::Gap12:
            if ((int32_t)(now - (_lRepStart + (uint32_t)(_lb1Dur + _lgap12))) >= 0)
                _listenPhase = ListenPhase::Burst2Window;
            break;

        case ListenPhase::Burst2Window:
            {
                uint8_t port0 = _pca->readPort0();
                _lb2Mask |= (~port0 & 0xFF);
                if ((int32_t)(now - (_lRepStart + (uint32_t)(_lb1Dur + _lgap12 + _lb2Dur))) >= 0) {
                    _lRepsDone++;
                    if (_lRepsDone >= _lReps) {
                        _listenPhase = ListenPhase::Idle;
                        _slotDone    = true;
                        Serial.printf("[IR] listen slot %d done b1=0x%02X b2=0x%02X\n",
                                      _listenSlotId, _lb1Mask, _lb2Mask);
                    } else {
                        _lRepStart  += perRep;
                        _listenPhase = ListenPhase::Burst1Window;
                    }
                }
            }
            break;

        default: break;
        }
    }

    bool isSlotDone() const { return _slotDone; }

    IrSlotResult takeSlotResult()
    {
        _slotDone = false;
        IrSlotResult r;
        r.slotId  = _listenSlotId;
        r.b1Mask  = _lb1Mask;
        r.b2Mask  = _lb2Mask;
        return r;
    }

private:
    MotorController_PCA9555* _pca = nullptr;

    // ---- Legacy emit state ----
    bool _emitting = false;

    // ---- Legacy listen state ----
    bool     _listening   = false;
    bool     _resultReady = false;
    uint32_t _listenEnd   = 0;
    uint8_t  _hitMask     = 0;

    // ---- Fire slot state machine ----
    enum class FirePhase : uint8_t {
        Idle, WaitingForStart, Burst1, Gap12, Burst2
    };
    bool      _fireActive = false;
    FirePhase _firePhase  = FirePhase::Idle;
    int       _fireSlotId = 0;
    uint32_t  _fireStart  = 0;
    uint32_t  _fRepStart  = 0;
    int       _fb1Dur     = 0;
    int       _fgap12     = 0;
    int       _fb2Dur     = 0;
    int       _frepGap    = 0;
    int       _fReps      = 0;
    int       _fRepsDone  = 0;

    // ---- Listen slot state machine ----
    enum class ListenPhase : uint8_t {
        Idle, WaitingForStart, Burst1Window, Gap12, Burst2Window
    };
    ListenPhase _listenPhase  = ListenPhase::Idle;
    bool        _slotDone     = false;
    int         _listenSlotId = 0;
    uint32_t    _listenStart  = 0;
    uint32_t    _lRepStart    = 0;
    int         _lb1Dur       = 0;
    int         _lgap12       = 0;
    int         _lb2Dur       = 0;
    int         _lrepGap      = 0;
    int         _lReps        = 0;
    int         _lRepsDone    = 0;
    uint8_t     _lb1Mask      = 0;
    uint8_t     _lb2Mask      = 0;

    // ---- Helpers ----
    void _ledOn()  { ledcWrite(IR_LEDC_CH1, 128); ledcWrite(IR_LEDC_CH2, 128); }
    void _ledOff() { ledcWrite(IR_LEDC_CH1, 0);   ledcWrite(IR_LEDC_CH2, 0);   }

    void _forceLedsOff()
    {
        // Detach and force pins LOW regardless of whether channels were attached.
        ledcDetachPin(IR_TX1_PIN);
        ledcDetachPin(IR_TX2_PIN);
        pinMode(IR_TX1_PIN, OUTPUT); digitalWrite(IR_TX1_PIN, LOW);
        pinMode(IR_TX2_PIN, OUTPUT); digitalWrite(IR_TX2_PIN, LOW);
    }

    void _endFireSlot()
    {
        _ledOff();
        ledcDetachPin(IR_TX1_PIN);
        ledcDetachPin(IR_TX2_PIN);
        pinMode(IR_TX1_PIN, OUTPUT); digitalWrite(IR_TX1_PIN, LOW);
        pinMode(IR_TX2_PIN, OUTPUT); digitalWrite(IR_TX2_PIN, LOW);
        _fireActive = false;
        _firePhase  = FirePhase::Idle;
        Serial.printf("[IR] fire slot %d done\n", _fireSlotId);
    }
};
