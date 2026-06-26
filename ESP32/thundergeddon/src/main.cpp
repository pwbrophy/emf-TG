// main.cpp — Thundergeddon ESP32-S3 robot firmware
//
// Boot sequence:
//   1. Init hardware (motors, IR, LEDs, camera config)
//   2. Connect Wi-Fi (blocking; required before anything else)
//   3. Start MJPEG server (bound to port 81; enabled later by stream_on)
//   4. Start OTA
//   5. Enter discovery loop — announce UDP, wait for server reply → WS URL
//   6. Connect WebSocket → send hello → enter main game loop
//
// Main loop responsibilities (all non-blocking):
//   - Motor soft-PWM tick every 1 ms
//   - LED effect / blink update every tick
//   - Buzzer duration management
//   - IR listen window polling
//   - WebSocket poll + heartbeat
//   - Wi-Fi health check every 10 s
//   - OTA service

#include <Arduino.h>
#include "driver/gpio.h"   // ESP-IDF GPIO — safe to call before Arduino init

// GPIO1 is the buzzer transistor base.  The ESP-IDF startup sequence and
// framework UART0 initialisation both run before Arduino's setup(), and either
// can briefly drive GPIO1 HIGH, latching the active buzzer on.
// A constructor with priority 101 runs earlier than any framework code and
// forces the pin LOW at the first safe moment after the GPIO driver is ready.
extern "C" void __attribute__((constructor(101))) buzzerEarlyLow()
{
    gpio_set_direction(GPIO_NUM_1, GPIO_MODE_OUTPUT);
    gpio_set_level(GPIO_NUM_1, 0);
}
#include <WiFi.h>
#include <WiFiUdp.h>
#include <ArduinoWebsockets.h>
#include <ArduinoJson.h>

#include <Preferences.h>
#include "secrets.h"                // WIFI_SSID, WIFI_PASS — gitignored
#include "OtaSupport.h"
#include "MotorController_PCA9555.h"
#include "IrController.h"
#include "LedController.h"
#include "CameraController.h"
#include "MjpegServer.h"
#include "RfidController.h"

using namespace websockets;

// ---- Pin assignments ----
static constexpr int I2C_SDA     = 21;
static constexpr int I2C_SCL     = 47;
static constexpr int BUZZER_PIN  = 1;   // MMBT3904 transistor base via 1 kΩ

// LEDC channel for buzzer tone — separate from camera (ch 0) and IR (ch 4-5)
static constexpr uint8_t BUZZER_LEDC_CH = 6;

// ---- Protocol timing ----
static constexpr uint16_t DISCOVERY_PORT = 30560;
static constexpr uint32_t ANNOUNCE_MS    = 2000;
static constexpr uint32_t HEARTBEAT_MS   = 2000;
static constexpr uint32_t MOTOR_TICK_MS  = 1;      // 1 ms → 125 Hz soft-PWM
static constexpr uint32_t WIFI_CHECK_MS  = 10000;  // reconnect check interval
// If no drive/turret command arrives for this long, coast motors to zero.
// Phone sends drive at 20 Hz (50 ms interval); 500 ms = 10× that, so transient
// Wi-Fi jitter won't false-trigger, but a real disconnect stops the robot quickly.
static constexpr uint32_t DRIVE_WATCHDOG_MS = 500;

// ---- Module instances ----
WiFiUDP                 udp;
WebsocketsClient        ws;
MotorController_PCA9555 motors;
IrController            ir;
LedController           leds;
CameraController        cam;
MjpegServer             mjpeg;
RfidController          rfid;

// ---- Global state ----
static String   g_robotId;
static String   g_wsUrl;           // empty = not yet discovered
static String   g_wsHost;
static int      g_wsPort  = 8080;
static String   g_wsPath  = "/esp32";
static bool     g_wsOpen  = false;

// Human-readable robot name — loaded from NVS on boot, updated by set_name command.
// Sent to the server in the hello message so any server knows the robot's name.
static String   g_robotName;

// Camera flip state — loaded from NVS on boot, updated by set_video_flip command.
// Both default to 1 to match the original hardcoded values in CameraController.
static int      g_hflip = 1;
static int      g_vflip = 1;

// Drive inversion state — loaded from NVS on boot, updated by set_drive_config command.
static int g_inv_throttle = 0;
static int g_inv_steer    = 0;
static int g_inv_turret   = 0;

// Video settings — loaded from NVS on boot, updated by set_video command.
// Frame size is an index into videoIdxToFrameSize(): 0=QVGA 1=CIF 2=HVGA 3=VGA.
static int g_videoFps          = 20;  // default 20 fps cap
static int g_videoFrameSizeIdx = 2;   // default HVGA 480x320 (matches old behaviour)
static int g_videoQuality      = 10;  // default JPEG quality

// Map the operator-facing frame-size index to an esp32-camera framesize_t.
static framesize_t videoIdxToFrameSize(int idx)
{
    switch (idx) {
        case 0: return FRAMESIZE_QVGA; // 320x240
        case 1: return FRAMESIZE_CIF;  // 400x296
        case 2: return FRAMESIZE_HVGA; // 480x320
        case 3: return FRAMESIZE_VGA;  // 640x480
        default: return FRAMESIZE_HVGA;
    }
}

// Set by PCA9555 INT ISR (GPIO14 FALLING); consumed by IrController::updateWindow().
// volatile so the compiler never caches it across loop iterations.
volatile bool   g_pca9555IntFired = false;

static uint32_t g_lastAnnounce   = 0;
static uint32_t g_lastHeartbeat  = 0;
static uint32_t g_lastMotorTick  = 0;
static uint32_t g_lastWifiCheck  = 0;
// Drive watchdog: 0 = no drive command yet this connection; >0 = millis() of last cmd.
// Turret has no watchdog — the turret pill sends only on change (not continuously),
// so a silent hold would falsely trigger.  A stray spinning turret is minor; a
// tank driving into walls is the real safety risk.
static uint32_t g_lastDriveTime  = 0;

// ---- Buzzer (simple timed tone) ----
static bool     g_buzzerEnabled = true;  // toggled via set_buzzer cmd
static uint32_t g_buzzerEnd    = 0;
static bool     g_buzzerActive = false;

static void silenceBuzzer()
{
    ledcDetachPin(BUZZER_PIN);
    pinMode(BUZZER_PIN, OUTPUT);
    digitalWrite(BUZZER_PIN, LOW);
}

static void playBuzzer(uint32_t freqHz, uint32_t durationMs)
{
    // Explicit channel so we don't clash with camera (ch 0) or IR (ch 4-5)
    ledcSetup(BUZZER_LEDC_CH, freqHz, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 128); // 50% duty
    g_buzzerEnd    = millis() + durationMs;
    g_buzzerActive = true;
}

static void updateBuzzer(uint32_t now)
{
    if (!g_buzzerActive) return;
    if ((int32_t)(now - g_buzzerEnd) >= 0) {
        silenceBuzzer();
        g_buzzerActive = false;
    }
}

// ---- Buzzer fire effect ----
// Phase 1 (0–200 ms):   staccato crackle — alternating 2–5 ms tone bursts and silence gaps at ~3.5 kHz.
// Phase 2 (200–600 ms): smooth exponential pitch-down 3000→100 Hz, volume decays to silence.
// Pitch is randomised ±50% each shot so no two shots sound identical.
static uint8_t  g_buzzerFirePhase     = 0; // 0=off, 1=bang, 2=tail
static uint32_t g_buzzerFireStart     = 0;
static uint32_t g_buzzerFireLastUp    = 0;
static float    g_buzzerFirePitchScale = 1.0f;
static uint32_t g_buzzerFireTailMs     = 400u;

static void startBuzzerFireEffect()
{
    if (!g_buzzerEnabled) return;
    g_buzzerFirePitchScale = 0.25f + (float)(random(276)) / 100.0f; // 0.25–3.0×
    g_buzzerFireTailMs     = 400u + (uint32_t)(random(401));         // 400–800 ms
    g_buzzerActive = false;
    uint32_t centreFreq = (uint32_t)(3500.0f * g_buzzerFirePitchScale);
    if (centreFreq > 20000) centreFreq = 20000;
    ledcSetup(BUZZER_LEDC_CH, centreFreq, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 128);
    g_buzzerFirePhase  = 1;
    g_buzzerFireStart  = millis();
    g_buzzerFireLastUp = millis();
}

static void updateBuzzerFireEffect(uint32_t now)
{
    if (g_buzzerFirePhase == 0) return;

    uint32_t elapsed = now - g_buzzerFireStart;

    // ---- Phase 1: pixelated static BANG (200 ms) ----
    if (g_buzzerFirePhase == 1) {
        if (elapsed >= 200) {
            g_buzzerFirePhase  = 2;
            g_buzzerFireLastUp = now;
            return;
        }
        if ((int32_t)(now - g_buzzerFireLastUp) >= 0) {
            if (random(2) == 0) {
                float centre = 3500.0f * g_buzzerFirePitchScale;
                int32_t rnd  = (int32_t)(((float)(random(2001) - 1000) / 1000.0f) * centre);
                int32_t raw  = (int32_t)centre + rnd;
                if (raw < 40)    raw = 40;
                if (raw > 20000) raw = 20000;
                ledcSetup(BUZZER_LEDC_CH, (uint32_t)raw, 8);
                ledcWrite(BUZZER_LEDC_CH, 128);
            } else {
                // silence gap — creates the staccato crackle texture
                ledcWrite(BUZZER_LEDC_CH, 0);
            }
            g_buzzerFireLastUp = now + 2u + (uint32_t)(random(4)); // 2–5 ms per block
        }
        return;
    }

    // ---- Phase 2: smooth exponential pitch-down (400 ms) ----
    if (g_buzzerFirePhase == 2) {
        uint32_t ph2 = elapsed - 200u;
        if (ph2 >= g_buzzerFireTailMs) {
            silenceBuzzer();
            g_buzzerFirePhase = 0;
            return;
        }
        if ((int32_t)(now - g_buzzerFireLastUp) >= 10) {
            g_buzzerFireLastUp = now;
            float t    = (float)ph2 / (float)g_buzzerFireTailMs;
            float freq = 3000.0f * g_buzzerFirePitchScale * expf(-3.4f * t);
            float duty = 128.0f  * expf(-5.0f  * t);
            ledcSetup(BUZZER_LEDC_CH, (uint32_t)freq, 8);
            ledcWrite(BUZZER_LEDC_CH, (uint8_t)duty);
        }
        return;
    }
}

// ---- Buzzer hit effect ----
// Phase 1 (0–50 ms):   clean sharp strike at 1200 Hz, full volume.
// Phase 2 (50–380 ms): pixelated falling noise — centre descends 1200→150 Hz,
//                       ±50 % random spread, 8 ms blocks, volume fades 128→0.
// Pitch is randomised ±50% each hit.
static uint8_t  g_buzzerHitPhase      = 0; // 0=off, 1=strike, 2=fall
static uint32_t g_buzzerHitStart      = 0;
static uint32_t g_buzzerHitNextBlock  = 0;
static float    g_buzzerHitPitchScale  = 1.0f;

// ---- Buzzer rear-hit effect ----
// Aggressive "OUCH" shriek for rear (3×) impacts.
// Phase 1 (0–30 ms):   loud crack at ~5000 Hz (scaled), duty=220.
// Phase 2 (30–450 ms): falling wail 7000→200 Hz, ±50% noise, 6 ms blocks, volume 220→0.
static uint8_t  g_buzzerRearHitPhase      = 0;
static uint32_t g_buzzerRearHitStart      = 0;
static uint32_t g_buzzerRearHitNextBlock  = 0;
static float    g_buzzerRearHitPitchScale  = 1.0f;

static void startBuzzerHitEffect()
{
    if (!g_buzzerEnabled) return;
    g_buzzerHitPitchScale = 0.25f + (float)(random(276)) / 100.0f; // 0.25–3.0×
    g_buzzerActive       = false;
    g_buzzerFirePhase    = 0;
    g_buzzerRearHitPhase = 0;
    uint32_t strikeFreq  = (uint32_t)(1200.0f * g_buzzerHitPitchScale);
    if (strikeFreq < 40)    strikeFreq = 40;
    if (strikeFreq > 20000) strikeFreq = 20000;
    ledcSetup(BUZZER_LEDC_CH, strikeFreq, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 128);
    g_buzzerHitPhase = 1;
    g_buzzerHitStart = millis();
}

static void updateBuzzerHitEffect(uint32_t now)
{
    if (g_buzzerHitPhase == 0) return;
    uint32_t elapsed = now - g_buzzerHitStart;

    if (g_buzzerHitPhase == 1) {
        if (elapsed >= 50) {
            g_buzzerHitPhase     = 2;
            g_buzzerHitNextBlock = now;
        }
        return;
    }

    if (g_buzzerHitPhase == 2) {
        uint32_t ph2 = elapsed - 50u;
        if (ph2 >= 330u) {
            silenceBuzzer();
            g_buzzerHitPhase = 0;
            return;
        }
        if ((int32_t)(now - g_buzzerHitNextBlock) >= 0) {
            float t      = (float)ph2 / 330.0f;
            float centre = (1200.0f - t * 1050.0f) * g_buzzerHitPitchScale;
            float spread = centre * 0.5f;
            int32_t rnd  = (int32_t)(((float)(random(2001) - 1000) / 1000.0f) * spread);
            int32_t raw  = (int32_t)centre + rnd;
            if (raw < 80)   raw = 80;
            if (raw > 8000) raw = 8000;
            uint8_t duty = (uint8_t)((1.0f - t) * 128.0f);
            ledcSetup(BUZZER_LEDC_CH, (uint32_t)raw, 8);
            ledcWrite(BUZZER_LEDC_CH, duty);
            g_buzzerHitNextBlock = now + 8; // 8 ms blocks for chunky pixel feel
        }
    }
}

static void startBuzzerRearHitEffect()
{
    if (!g_buzzerEnabled) return;
    g_buzzerRearHitPitchScale = 0.75f + (float)(random(51)) / 100.0f; // 0.75–1.25×
    g_buzzerActive    = false;
    g_buzzerFirePhase = 0;
    g_buzzerHitPhase  = 0;
    uint32_t startFreq = (uint32_t)(4000.0f * g_buzzerRearHitPitchScale);
    ledcSetup(BUZZER_LEDC_CH, startFreq, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 0);
    g_buzzerRearHitPhase     = 1;
    g_buzzerRearHitStart     = millis();
    g_buzzerRearHitNextBlock = millis();
}

static void updateBuzzerRearHitEffect(uint32_t now)
{
    if (g_buzzerRearHitPhase == 0) return;
    uint32_t elapsed = now - g_buzzerRearHitStart;

    // Phase 1 (0–180 ms): triangle pitch sweep 4000→10000→4000 Hz, volume 0→220→0.
    if (g_buzzerRearHitPhase == 1) {
        const uint32_t P1_MS = 180u;
        if (elapsed >= P1_MS) {
            g_buzzerRearHitPhase = 2;
            g_buzzerRearHitNextBlock = now;
            return;
        }
        if ((int32_t)(now - g_buzzerRearHitNextBlock) >= 0) {
            float t      = (float)elapsed / (float)P1_MS;
            float tri    = (t < 0.5f) ? (t * 2.0f) : ((1.0f - t) * 2.0f);
            float centre = (4000.0f + tri * 6000.0f) * g_buzzerRearHitPitchScale;
            float spread = centre * 0.2f;
            int32_t rnd  = (int32_t)(((float)(random(2001) - 1000) / 1000.0f) * spread);
            int32_t raw  = (int32_t)centre + rnd;
            if (raw < 60)    raw = 60;
            if (raw > 20000) raw = 20000;
            ledcSetup(BUZZER_LEDC_CH, (uint32_t)raw, 8);
            ledcWrite(BUZZER_LEDC_CH, (uint8_t)(tri * 220.0f));
            g_buzzerRearHitNextBlock = now + 4;
        }
        return;
    }

    // Phase 2 (180–880 ms): rising squeak tail, 4000→12000 Hz climbing as it fades.
    if (g_buzzerRearHitPhase == 2) {
        const uint32_t P2_MS = 700u;
        uint32_t ph2 = elapsed - 180u;
        if (ph2 >= P2_MS) {
            silenceBuzzer();
            g_buzzerRearHitPhase = 0;
            return;
        }
        if ((int32_t)(now - g_buzzerRearHitNextBlock) >= 0) {
            float t      = (float)ph2 / (float)P2_MS;
            float centre = (4000.0f + t * 8000.0f) * g_buzzerRearHitPitchScale; // rises 4000→12000 Hz
            float spread = centre * 0.12f;
            int32_t rnd  = (int32_t)(((float)(random(2001) - 1000) / 1000.0f) * spread);
            int32_t raw  = (int32_t)centre + rnd;
            if (raw < 500)   raw = 500;
            if (raw > 20000) raw = 20000;
            ledcSetup(BUZZER_LEDC_CH, (uint32_t)raw, 8);
            ledcWrite(BUZZER_LEDC_CH, (uint8_t)((1.0f - t) * 180.0f));
            g_buzzerRearHitNextBlock = now + 4;
        }
    }
}

// ---- Buzzer heal effect ----
// Phase 1 (0–700 ms):   linear rising sweep 200→1500 Hz, volume 0→128.
// Phase 2 (700–800 ms): hold at 1500 Hz, full volume.
// Phase 3 (820–1220 ms): 4 ascending pips (1600 / 1900 / 2300 / 2800 Hz, 55 ms on / 45 ms off).
static uint8_t  g_buzzerHealPhase   = 0; // 0=off, 1=ramp, 2=hold, 3=pips
static uint32_t g_buzzerHealStart   = 0;
static uint32_t g_buzzerHealLastUp  = 0;
static uint8_t  g_buzzerHealCurPip  = 255;

static const uint32_t HEAL_PIP_FREQS[4]  = {1600, 1900, 2300, 2800};
static const uint8_t  HEAL_PIP_DUTIES[4] = {120,  135,  155,  175};

static void startBuzzerHealEffect()
{
    if (!g_buzzerEnabled) return;
    g_buzzerActive    = false;
    g_buzzerFirePhase = 0;
    g_buzzerHitPhase  = 0;
    ledcSetup(BUZZER_LEDC_CH, 200, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 0);
    g_buzzerHealPhase  = 1;
    g_buzzerHealStart  = millis();
    g_buzzerHealLastUp = millis();
}

static void updateBuzzerHealEffect(uint32_t now)
{
    if (g_buzzerHealPhase == 0) return;
    uint32_t elapsed = now - g_buzzerHealStart;

    if (g_buzzerHealPhase == 1) {
        if (elapsed >= 700) {
            ledcSetup(BUZZER_LEDC_CH, 1500, 8);
            ledcWrite(BUZZER_LEDC_CH, 128);
            g_buzzerHealPhase = 2;
            return;
        }
        if ((int32_t)(now - g_buzzerHealLastUp) >= 10) {
            g_buzzerHealLastUp = now;
            float t = (float)elapsed / 700.0f;
            uint32_t freq = 200u + (uint32_t)(t * 1300.0f); // 200→1500 Hz
            uint8_t  duty = (uint8_t)(t * 128.0f);           // 0→128
            ledcSetup(BUZZER_LEDC_CH, freq, 8);
            ledcWrite(BUZZER_LEDC_CH, duty);
        }
        return;
    }

    if (g_buzzerHealPhase == 2) {
        if (elapsed >= 800) {
            ledcWrite(BUZZER_LEDC_CH, 0); // brief silence before pips
            g_buzzerHealPhase  = 3;
            g_buzzerHealCurPip = 255;
        }
        return;
    }

    if (g_buzzerHealPhase == 3) {
        // 4 pips starting at 820 ms; each slot 100 ms (55 ms on, 45 ms off).
        const uint32_t PIP_BASE = 820u;
        const uint32_t PIP_SLOT = 100u;
        const uint32_t PIP_ON   = 55u;

        if (elapsed >= PIP_BASE + 4u * PIP_SLOT) {
            silenceBuzzer();
            g_buzzerHealPhase = 0;
            return;
        }
        if (elapsed < PIP_BASE) return; // small pre-pip gap

        uint32_t pipElapsed = elapsed - PIP_BASE;
        uint8_t  pipIdx     = (uint8_t)(pipElapsed / PIP_SLOT);
        uint32_t pipOff     = pipElapsed % PIP_SLOT;

        if (pipIdx != g_buzzerHealCurPip) {
            g_buzzerHealCurPip = pipIdx;
            ledcSetup(BUZZER_LEDC_CH, HEAL_PIP_FREQS[pipIdx], 8);
        }
        ledcWrite(BUZZER_LEDC_CH, pipOff < PIP_ON ? HEAL_PIP_DUTIES[pipIdx] : 0);
    }
}

// ---- Buzzer death explosion effect ----
// Phase 1 (0–80 ms):    high-pitched crack — blocky noise 5000–12000 Hz, 3 ms blocks.
// Phase 2 (80–5000 ms): deep rumble descending 2000→80 Hz, fading to silence over 4920 ms.
static uint8_t  g_buzzerDeathPhase     = 0; // 0=off, 1=crack, 2=rumble
static uint32_t g_buzzerDeathStart     = 0;
static uint32_t g_buzzerDeathNextNoise = 0;

static void startBuzzerDeathEffect()
{
    if (!g_buzzerEnabled) return;
    g_buzzerActive    = false;
    g_buzzerFirePhase = 0;
    g_buzzerHitPhase  = 0;
    g_buzzerHealPhase = 0;
    ledcSetup(BUZZER_LEDC_CH, 8000, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 255);
    g_buzzerDeathPhase     = 1;
    g_buzzerDeathStart     = millis();
    g_buzzerDeathNextNoise = millis() + 3; // first noise block after 3 ms
}

static void updateBuzzerDeathEffect(uint32_t now)
{
    if (g_buzzerDeathPhase == 0) return;

    uint32_t elapsed = now - g_buzzerDeathStart;

    // ---- Phase 1: crack (0–80 ms), rapid noise blocks ----
    if (g_buzzerDeathPhase == 1) {
        if (elapsed >= 80u) {
            g_buzzerDeathPhase     = 2;
            g_buzzerDeathNextNoise = now;
            return;
        }
        if ((int32_t)(now - g_buzzerDeathNextNoise) >= 0) {
            // Random freq in 5000–12000 Hz range
            uint32_t freq = 5000u + (uint32_t)(random(7001));
            ledcSetup(BUZZER_LEDC_CH, freq, 8);
            ledcWrite(BUZZER_LEDC_CH, 255);
            g_buzzerDeathNextNoise = now + 3; // 3 ms blocks
        }
        return;
    }

    // ---- Phase 2: descending rumble (80–5000 ms) ----
    if (g_buzzerDeathPhase == 2) {
        uint32_t p2 = elapsed - 80u;
        if (p2 >= 4920u) {
            silenceBuzzer();
            g_buzzerDeathPhase = 0;
            return;
        }
        if ((int32_t)(now - g_buzzerDeathNextNoise) >= 0) {
            float t      = (float)p2 / 4920.0f;          // 0→1
            float centre = 2000.0f - t * 1920.0f;        // 2000→80 Hz
            float spread = centre * 0.4f;
            int32_t rnd  = (int32_t)(((float)(random(2001) - 1000) / 1000.0f) * spread);
            int32_t raw  = (int32_t)centre + rnd;
            if (raw < 60)    raw = 60;
            if (raw > 20000) raw = 20000;

            uint8_t duty = (uint8_t)((1.0f - t) * 200.0f);
            ledcSetup(BUZZER_LEDC_CH, (uint32_t)raw, 8);
            ledcWrite(BUZZER_LEDC_CH, duty);

            uint32_t blockMs = 3u + (uint32_t)(t * 22.0f); // 3→25 ms blocks
            g_buzzerDeathNextNoise = now + blockMs;
        }
    }
}

// ---- Buzzer capture effect ----
// 3-note ascending C-major arpeggio: C5 (523 Hz) → E5 (659 Hz) → G5 (784 Hz).
// Each note slot is 110 ms (85 ms on, 25 ms off).  Total 330 ms.
static uint8_t  g_buzzerCapturePhase  = 0; // 0=off, 1=playing
static uint32_t g_buzzerCaptureStart  = 0;
static uint8_t  g_buzzerCapturePip    = 255;

static const uint32_t CAPTURE_PIP_FREQS[3]  = {523, 659, 784};
static const uint8_t  CAPTURE_PIP_DUTIES[3] = {150, 160, 175};

static void startBuzzerCaptureEffect()
{
    if (!g_buzzerEnabled) return;
    g_buzzerActive      = false;
    g_buzzerFirePhase   = 0;
    g_buzzerHitPhase    = 0;
    g_buzzerHealPhase   = 0;
    g_buzzerDeathPhase  = 0;
    ledcSetup(BUZZER_LEDC_CH, CAPTURE_PIP_FREQS[0], 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 0);
    g_buzzerCapturePhase = 1;
    g_buzzerCaptureStart = millis();
    g_buzzerCapturePip   = 255;
}

static void updateBuzzerCaptureEffect(uint32_t now)
{
    if (g_buzzerCapturePhase == 0) return;

    const uint32_t PIP_SLOT = 110u;
    const uint32_t PIP_ON   = 85u;
    uint32_t elapsed = now - g_buzzerCaptureStart;

    if (elapsed >= 3u * PIP_SLOT) {
        silenceBuzzer();
        g_buzzerCapturePhase = 0;
        return;
    }

    uint8_t  pipIdx = (uint8_t)(elapsed / PIP_SLOT);
    uint32_t pipOff = elapsed % PIP_SLOT;

    if (pipIdx != g_buzzerCapturePip) {
        g_buzzerCapturePip = pipIdx;
        ledcSetup(BUZZER_LEDC_CH, CAPTURE_PIP_FREQS[pipIdx], 8);
        ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    }
    ledcWrite(BUZZER_LEDC_CH, pipOff < PIP_ON ? CAPTURE_PIP_DUTIES[pipIdx] : 0);
}

// ---- Buzzer countdown tick effect ----
// Sine-bell envelope: smooth fade-in → fade-out over CDOWN_TOTAL_MS.
// baseFreq and targetDuty both rise each tick (pitch and volume increase toward game start).
static uint8_t  g_buzzerCountdownPhase      = 0; // 0=off, 1=playing
static uint32_t g_buzzerCountdownStart      = 0;
static uint32_t g_buzzerCountdownBaseFreq   = 100;
static uint8_t  g_buzzerCountdownTargetDuty = 100;

static const uint32_t CDOWN_TOTAL_MS = 110u;

static void startBuzzerCountdownTick(uint32_t baseFreq, uint8_t targetDuty)
{
    if (!g_buzzerEnabled) return;
    g_buzzerActive              = false;
    g_buzzerCountdownBaseFreq   = baseFreq;
    g_buzzerCountdownTargetDuty = targetDuty;
    ledcSetup(BUZZER_LEDC_CH, baseFreq, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 0);
    g_buzzerCountdownPhase = 1;
    g_buzzerCountdownStart = millis();
}

static void updateBuzzerCountdownEffect(uint32_t now)
{
    if (g_buzzerCountdownPhase == 0) return;
    uint32_t elapsed = now - g_buzzerCountdownStart;
    if (elapsed >= CDOWN_TOTAL_MS) {
        silenceBuzzer();
        g_buzzerCountdownPhase = 0;
        return;
    }
    float t      = (float)elapsed / (float)CDOWN_TOTAL_MS;
    float tri    = (t < 0.5f) ? (t * 2.0f) : ((1.0f - t) * 2.0f);
    uint8_t duty = (uint8_t)((float)g_buzzerCountdownTargetDuty * tri);
    ledcWrite(BUZZER_LEDC_CH, duty);
}

// ---- Buzzer game-start fanfare ----
// 4 ascending pips: 500 → 700 → 1000 → 1400 Hz, 90 ms on / 20 ms off per pip.
static uint8_t  g_buzzerFanfarePhase = 0; // 0=off, 1=playing
static uint32_t g_buzzerFanfareStart = 0;
static uint8_t  g_buzzerFanfarePip   = 255;

static const uint32_t FANFARE_PIP_FREQS[4]  = {500, 700, 1000, 1400};
static const uint8_t  FANFARE_PIP_DUTIES[4] = {140, 155, 170,  200};

static void startBuzzerFanfareEffect()
{
    if (!g_buzzerEnabled) return;
    g_buzzerActive          = false;
    g_buzzerFirePhase       = 0;
    g_buzzerHitPhase        = 0;
    g_buzzerHealPhase       = 0;
    g_buzzerDeathPhase      = 0;
    g_buzzerCapturePhase    = 0;
    g_buzzerCountdownPhase  = 0;
    ledcSetup(BUZZER_LEDC_CH, FANFARE_PIP_FREQS[0], 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 0);
    g_buzzerFanfarePhase = 1;
    g_buzzerFanfareStart = millis();
    g_buzzerFanfarePip   = 255;
}

static void updateBuzzerFanfareEffect(uint32_t now)
{
    if (g_buzzerFanfarePhase == 0) return;

    const uint32_t PIP_SLOT = 110u; // 90 ms on + 20 ms off
    const uint32_t PIP_ON   = 90u;
    uint32_t elapsed = now - g_buzzerFanfareStart;

    if (elapsed >= 4u * PIP_SLOT) {
        silenceBuzzer();
        g_buzzerFanfarePhase = 0;
        return;
    }

    uint8_t  pipIdx = (uint8_t)(elapsed / PIP_SLOT);
    uint32_t pipOff = elapsed % PIP_SLOT;

    if (pipIdx != g_buzzerFanfarePip) {
        g_buzzerFanfarePip = pipIdx;
        ledcSetup(BUZZER_LEDC_CH, FANFARE_PIP_FREQS[pipIdx], 8);
        ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    }
    ledcWrite(BUZZER_LEDC_CH, pipOff < PIP_ON ? FANFARE_PIP_DUTIES[pipIdx] : 0);
}

// ---- Boot beeps: double blip (blip + pitched-up blip) ----
// Each boot beep = 35 ms blip at base freq, 12 ms silence, 35 ms blip at freq×1.4.
// Triangle envelope (sharp peak) on each blip.
static const uint32_t BOOTBLIP_ON_MS  = 35u;
static const uint32_t BOOTBLIP_GAP_MS = 12u;
static const float    BOOTBLIP_LO     = 0.5f;  // first blip — octave below
static const float    BOOTBLIP_HI     = 1.4f;  // second blip — higher than base

// Blocking version — call only from setup().
static void playBuzzerBootBlip(uint32_t freqHz)
{
    if (!g_buzzerEnabled) return;
    uint32_t freqLo = (uint32_t)((float)freqHz * BOOTBLIP_LO);
    uint32_t freqHi = (uint32_t)((float)freqHz * BOOTBLIP_HI);
    if (freqLo < 40)    freqLo = 40;
    if (freqHi > 20000) freqHi = 20000;

    ledcSetup(BUZZER_LEDC_CH, freqLo, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    uint32_t start = millis();
    while (millis() - start < BOOTBLIP_ON_MS) {
        float t   = (float)(millis() - start) / (float)BOOTBLIP_ON_MS;
        float tri = (t < 0.5f) ? (t * 2.0f) : ((1.0f - t) * 2.0f);
        ledcWrite(BUZZER_LEDC_CH, (uint8_t)(128.0f * tri));
        delay(3);
    }
    ledcWrite(BUZZER_LEDC_CH, 0);
    delay(BOOTBLIP_GAP_MS);

    ledcSetup(BUZZER_LEDC_CH, freqHi, 8);
    start = millis();
    while (millis() - start < BOOTBLIP_ON_MS) {
        float t   = (float)(millis() - start) / (float)BOOTBLIP_ON_MS;
        float tri = (t < 0.5f) ? (t * 2.0f) : ((1.0f - t) * 2.0f);
        ledcWrite(BUZZER_LEDC_CH, (uint8_t)(128.0f * tri));
        delay(3);
    }
    silenceBuzzer();
}

// Non-blocking version — for the server-connected beep (fires inside loop).
static uint8_t  g_buzzerBootBlipPhase  = 0; // 0=off 1=blip1 2=gap 3=blip2
static uint32_t g_buzzerBootBlipStart  = 0;
static uint32_t g_buzzerBootBlipFreqHz = 0;

static void startBuzzerBootBlip(uint32_t freqHz)
{
    if (!g_buzzerEnabled) return;
    g_buzzerActive        = false;
    g_buzzerFirePhase     = 0;
    g_buzzerHitPhase      = 0;
    g_buzzerRearHitPhase  = 0;
    g_buzzerBootBlipFreqHz = freqHz;
    uint32_t freqLo = (uint32_t)((float)freqHz * BOOTBLIP_LO);
    if (freqLo < 40) freqLo = 40;
    ledcSetup(BUZZER_LEDC_CH, freqLo, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 0);
    g_buzzerBootBlipStart = millis();
    g_buzzerBootBlipPhase = 1;
}

static void updateBuzzerBootBlip(uint32_t now)
{
    if (g_buzzerBootBlipPhase == 0) return;
    uint32_t elapsed = now - g_buzzerBootBlipStart;

    if (g_buzzerBootBlipPhase == 1) {
        if (elapsed >= BOOTBLIP_ON_MS) { ledcWrite(BUZZER_LEDC_CH, 0); g_buzzerBootBlipPhase = 2; return; }
        float t   = (float)elapsed / (float)BOOTBLIP_ON_MS;
        float tri = (t < 0.5f) ? (t * 2.0f) : ((1.0f - t) * 2.0f);
        ledcWrite(BUZZER_LEDC_CH, (uint8_t)(128.0f * tri));
        return;
    }
    if (g_buzzerBootBlipPhase == 2) {
        if (elapsed >= BOOTBLIP_ON_MS + BOOTBLIP_GAP_MS) {
            uint32_t freqHi = (uint32_t)((float)g_buzzerBootBlipFreqHz * BOOTBLIP_HI);
            if (freqHi > 20000) freqHi = 20000;
            ledcSetup(BUZZER_LEDC_CH, freqHi, 8);
            ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
            ledcWrite(BUZZER_LEDC_CH, 0);
            g_buzzerBootBlipPhase = 3;
        }
        return;
    }
    if (g_buzzerBootBlipPhase == 3) {
        uint32_t ph3 = elapsed - BOOTBLIP_ON_MS - BOOTBLIP_GAP_MS;
        if (ph3 >= BOOTBLIP_ON_MS) { silenceBuzzer(); g_buzzerBootBlipPhase = 0; return; }
        float t   = (float)ph3 / (float)BOOTBLIP_ON_MS;
        float tri = (t < 0.5f) ? (t * 2.0f) : ((1.0f - t) * 2.0f);
        ledcWrite(BUZZER_LEDC_CH, (uint8_t)(128.0f * tri));
    }
}

// ---- OTA audio/LED callbacks ----

static void otaProgressCb(uint8_t pct)
{
    leds.showOtaProgress(pct);
}

static void otaEndCb()
{
    // Victory pips: C5 → E5 → G5 (blocking — device reboots immediately after)
    ledcSetup(BUZZER_LEDC_CH, 523, 8); ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 28); delay(18);
    ledcWrite(BUZZER_LEDC_CH, 0);  delay(10);
    ledcSetup(BUZZER_LEDC_CH, 659, 8);
    ledcWrite(BUZZER_LEDC_CH, 28); delay(18);
    ledcWrite(BUZZER_LEDC_CH, 0);  delay(10);
    ledcSetup(BUZZER_LEDC_CH, 784, 8);
    ledcWrite(BUZZER_LEDC_CH, 28); delay(22);
    ledcWrite(BUZZER_LEDC_CH, 0);
    leds.showOtaProgress(100);
}

// ---- Robot ID (12-char uppercase MAC hex, no separators) ----
static String makeRobotId()
{
    uint64_t mac = ESP.getEfuseMac();
    char buf[13];
    snprintf(buf, sizeof(buf), "%02X%02X%02X%02X%02X%02X",
             (uint8_t)(mac >> 40), (uint8_t)(mac >> 32),
             (uint8_t)(mac >> 24), (uint8_t)(mac >> 16),
             (uint8_t)(mac >>  8), (uint8_t)(mac >>  0));
    return String(buf);
}

// ---- Wi-Fi ----
static void connectWifi()
{
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false);           // low-latency radio; important for drive commands

    const int NET_COUNT = sizeof(WIFI_NETWORKS) / sizeof(WIFI_NETWORKS[0]);
    const unsigned long ATTEMPT_MS = 15000; // max ms before moving to next network

    while (true) {
        for (int i = 0; i < NET_COUNT; i++) {
            Serial.printf("[WIFI] trying %s\n", WIFI_NETWORKS[i].ssid);
            WiFi.begin(WIFI_NETWORKS[i].ssid, WIFI_NETWORKS[i].pass);
            unsigned long start = millis();
            while (millis() - start < ATTEMPT_MS) {
                wl_status_t s = WiFi.status();
                if (s == WL_CONNECTED) {
                    Serial.printf("[WIFI] connected to %s, IP=%s\n",
                        WIFI_NETWORKS[i].ssid,
                        WiFi.localIP().toString().c_str());
                    return;
                }
                if (s == WL_NO_SSID_AVAIL || s == WL_CONNECT_FAILED) break;
                leds.update(millis());
                delay(100);
            }
            Serial.printf("[WIFI] %s not available\n", WIFI_NETWORKS[i].ssid);
            WiFi.disconnect(true);
            leds.update(millis());
            delay(100);
        }
    }
}

// ---- UDP discovery ----
static void startDiscovery()
{
    udp.stop();
    // Retry bind up to 5 times with a short delay — the port may briefly sit in
    // TIME_WAIT after a rapid connect/close cycle (e.g. server restart mid-session).
    for (int attempt = 0; attempt < 5; attempt++) {
        if (udp.begin(DISCOVERY_PORT)) {
            g_lastAnnounce = 0; // force immediate announce on next loop tick
            Serial.println("[DISCOVERY] listening");
            return;
        }
        Serial.printf("[DISCOVERY] bind failed (attempt %d/5), retrying...\n", attempt + 1);
        delay(200);
    }
    Serial.println("[DISCOVERY] bind failed permanently — will retry next onWsClose");
}

static void maybeAnnounce()
{
    const uint32_t now = millis();
    if (now - g_lastAnnounce < ANNOUNCE_MS) return;
    g_lastAnnounce = now;

    // Include IP so the server has it immediately without a separate lookup
    String payload = String("{\"robotId\":\"") + g_robotId +
                     "\",\"callsign\":\"\",\"ip\":\"" +
                     WiFi.localIP().toString() + "\"}";
    udp.beginPacket(IPAddress(255,255,255,255), DISCOVERY_PORT);
    udp.write((const uint8_t*)payload.c_str(), payload.length());
    udp.endPacket();
    Serial.println("[DISCOVERY] announce");
}

// Returns true and populates g_wsUrl when a valid server reply is received.
static bool readDiscoveryReply()
{
    int sz = udp.parsePacket();
    if (sz <= 0) return false;

    char buf[256];
    int n = udp.read(buf, sizeof(buf) - 1);
    if (n <= 0) return false;
    buf[n] = '\0';

    String s(buf);
    int k  = s.indexOf("\"ws\"");   if (k  < 0) return false;
    int c  = s.indexOf(':', k);     if (c  < 0) return false;
    int q1 = s.indexOf('"', c+1);   if (q1 < 0) return false;
    int q2 = s.indexOf('"', q1+1);  if (q2 < 0) return false;

    g_wsUrl = s.substring(q1+1, q2);
    Serial.print("[DISCOVERY] ws="); Serial.println(g_wsUrl);

    // Parse ws://host:port/path into components so we can use the three-arg
    // ws.connect(host, port, path) overload, which bypasses the URL parser.
    {
        // Skip "ws://"
        int hStart = g_wsUrl.indexOf("://");
        hStart = (hStart >= 0) ? hStart + 3 : 0;
        int colon = g_wsUrl.indexOf(':', hStart);
        int slash  = g_wsUrl.indexOf('/', hStart);
        if (colon > 0 && slash > colon) {
            g_wsHost = g_wsUrl.substring(hStart, colon);
            g_wsPort = g_wsUrl.substring(colon + 1, slash).toInt();
            g_wsPath = g_wsUrl.substring(slash);
        }
        Serial.printf("[DISCOVERY] host=%s port=%d path=%s\n",
                      g_wsHost.c_str(), g_wsPort, g_wsPath.c_str());
    }

    return true;
}

// ---- WebSocket — clean up when connection drops ----
static void onWsClose()
{
    if (!g_wsOpen && g_wsUrl.isEmpty()) return; // already cleaned up
    g_wsOpen = false;
    g_lastDriveTime = 0; // reset watchdog — motors are about to be disabled
    motors.enable(false);
    ir.stopEmit();
    mjpeg.setEnabled(false);
    if (cam.isStarted()) cam.stop();
    leds.setStatus(StatusPattern::SearchingFast);
    leds.setBootPhase(BootPhase::ServerConnecting);
    g_wsUrl.clear();
    startDiscovery();
    Serial.println("[WS] closed → rediscover");
}

// ---- WebSocket — incoming command dispatcher ----
static void handleWsText(const String& s)
{
    Serial.print("[WS] rx: "); Serial.println(s);

    JsonDocument doc;
    if (deserializeJson(doc, s) != DeserializationError::Ok) {
        Serial.println("[WS] JSON parse error");
        return;
    }

    const char* cmd = doc["cmd"] | "";

    // ---- Drive / turret ----

    if (strcmp(cmd, "drive") == 0) {
        float l = doc["l"] | 0.0f;
        float r = doc["r"] | 0.0f;
        if (g_inv_throttle) { l = -l; r = -r; }
        if (g_inv_steer)    { float t = l; l = r; r = t; }
        motors.setLeftRight(r, l); // hardware swap: left track wired to turret connector
        g_lastDriveTime = millis();
        return;
    }

    if (strcmp(cmd, "turret") == 0) {
        float v = doc["speed"] | 0.0f;
        if (g_inv_turret) v = -v;
        motors.setTurret(v);
        return;
    }

    // ---- Motor enable ----

    if (strcmp(cmd, "motors_on") == 0) {
        motors.enable(true);
        Serial.println("[MOT] on");
        return;
    }

    if (strcmp(cmd, "motors_off") == 0) {
        motors.enable(false);
        Serial.println("[MOT] off");
        return;
    }

    // ---- Camera stream ----

    if (strcmp(cmd, "stream_on") == 0) {
        if (!cam.isStarted() && !cam.start()) {
            Serial.println("[CAM] start failed");
            return;
        }
        mjpeg.setEnabled(true);
        leds.setStatus(StatusPattern::InGameSolid);
        Serial.println("[CAM] stream ON");
        return;
    }

    if (strcmp(cmd, "stream_off") == 0) {
        mjpeg.setEnabled(false);
        leds.setStatus(StatusPattern::ConnectedSlow);
        Serial.println("[CAM] stream OFF");
        return;
    }

    if (strcmp(cmd, "set_video_flip") == 0) {
        g_hflip = doc["h"] | g_hflip;
        g_vflip = doc["v"] | g_vflip;
        cam.applyFlip(g_vflip != 0, g_hflip != 0);
        Preferences prefs;
        prefs.begin("tg_cam", false); // read-write
        prefs.putInt("hflip", g_hflip);
        prefs.putInt("vflip", g_vflip);
        prefs.end();
        Serial.printf("[CAM] flip set hflip=%d vflip=%d\n", g_hflip, g_vflip);
        return;
    }

    if (strcmp(cmd, "set_video") == 0) {
        g_videoFps          = doc["fps"]       | g_videoFps;
        g_videoFrameSizeIdx = doc["framesize"] | g_videoFrameSizeIdx;
        g_videoQuality      = doc["quality"]   | g_videoQuality;
        mjpeg.setMaxFps(g_videoFps);
        cam.setFrameSize(videoIdxToFrameSize(g_videoFrameSizeIdx));
        cam.setQuality(g_videoQuality);
        Preferences prefs;
        prefs.begin("tg_cam", false); // read-write
        prefs.putInt("fps",      g_videoFps);
        prefs.putInt("fsize",    g_videoFrameSizeIdx);
        prefs.putInt("fquality", g_videoQuality);
        prefs.end();
        Serial.printf("[CAM] set_video fps=%d fsize=%d quality=%d\n",
                      g_videoFps, g_videoFrameSizeIdx, g_videoQuality);
        return;
    }

    if (strcmp(cmd, "set_drive_config") == 0) {
        g_inv_throttle = doc["inv_throttle"] | g_inv_throttle;
        g_inv_steer    = doc["inv_steer"]    | g_inv_steer;
        g_inv_turret   = doc["inv_turret"]   | g_inv_turret;
        Preferences prefs;
        prefs.begin("tg_drv", false);
        prefs.putInt("inv_throttle", g_inv_throttle);
        prefs.putInt("inv_steer",    g_inv_steer);
        prefs.putInt("inv_turret",   g_inv_turret);
        prefs.end();
        Serial.printf("[DRV] config set inv_throttle=%d inv_steer=%d inv_turret=%d\n",
                      g_inv_throttle, g_inv_steer, g_inv_turret);
        return;
    }

    if (strcmp(cmd, "set_physics") == 0) {
        float da = doc["drive_accel"]  | 0.0f;
        float dd = doc["drive_decel"]  | 0.0f;
        float ta = doc["turret_accel"] | 0.0f;
        float td = doc["turret_decel"] | 0.0f;
        motors.setPhysics(da, dd, ta, td);
        Serial.printf("[PHYS] drive=%.2f/%.2f turret=%.2f/%.2f\n", da, dd, ta, td);
        return;
    }

    if (strcmp(cmd, "set_name") == 0) {
        const char* newName = doc["name"] | "";
        g_robotName = String(newName);
        Preferences prefs;
        prefs.begin("tg_id", false);
        prefs.putString("name", g_robotName);
        prefs.end();
        Serial.printf("[ID] Name set to: %s\n", g_robotName.c_str());
        return;
    }

    // ---- Game effects ----

    if (strcmp(cmd, "flash_fire") == 0) {
        leds.fireSequence();
        startBuzzerFireEffect();
        return;
    }

    if (strcmp(cmd, "flash_hit") == 0) {
        leds.flashHit();
        if (doc["rear"] | false)
            startBuzzerRearHitEffect();
        else
            startBuzzerHitEffect();
        return;
    }

    if (strcmp(cmd, "flash_heal") == 0) {
        leds.healCharge();
        startBuzzerHealEffect();
        return;
    }

    if (strcmp(cmd, "flash_death") == 0) {
        leds.deathExplosion();
        startBuzzerDeathEffect();
        return;
    }

    if (strcmp(cmd, "flash_capture") == 0) {
        leds.captureSequence();
        startBuzzerCaptureEffect();
        return;
    }

    if (strcmp(cmd, "invuln_start") == 0) {
        leds.startInvuln();
        return;
    }

    if (strcmp(cmd, "invuln_end") == 0) {
        leds.stopInvuln();
        return;
    }

    if (strcmp(cmd, "set_hp") == 0) {
        leds.setHp(doc["hp"] | 0, doc["max"] | 100);
        return;
    }

    // ---- Generic flash (used by operator test panel) ----

    if (strcmp(cmd, "flash") == 0) {
        leds.flashFire((uint32_t)(doc["ms"] | 200));
        return;
    }

    // ---- IR emit ----

    if (strcmp(cmd, "ir_emit_stop") == 0) {
        ir.stopEmit();
        motors.enable(true);
        Serial.println("[IR] emit_stop");
        return;
    }

    // ---- Handshake IR emit (ACK-driven, no clock sync) ----

    if (strcmp(cmd, "ir_emit_left") == 0) {
        ir.beginEmitLeft();
        ws.send("{\"cmd\":\"ir_emit_ack\"}");
        Serial.println("[IR] emit_left → ir_emit_ack sent");
        return;
    }

    if (strcmp(cmd, "ir_emit_right") == 0) {
        ir.beginEmitRight();
        ws.send("{\"cmd\":\"ir_emit_ack\"}");
        Serial.println("[IR] emit_right → ir_emit_ack sent");
        return;
    }

    if (strcmp(cmd, "ir_listen_window") == 0) {
        int ms = doc["ms"] | 100;
        ir.beginListenWindow((uint32_t)(ms > 0 ? ms : 100));
        Serial.printf("[IR] listen_window %d ms\n", ms);
        return;
    }

    // ---- Buzzer mute ----

    if (strcmp(cmd, "set_buzzer") == 0) {
        g_buzzerEnabled = (doc["enabled"] | 1) != 0;
        if (!g_buzzerEnabled) {
            // Silence any currently-playing effect
            g_buzzerActive         = false;
            g_buzzerFirePhase      = 0;
            g_buzzerHitPhase       = 0;
            g_buzzerRearHitPhase   = 0;
            g_buzzerHealPhase      = 0;
            g_buzzerDeathPhase     = 0;
            g_buzzerCapturePhase   = 0;
            g_buzzerCountdownPhase = 0;
            g_buzzerFanfarePhase   = 0;
            g_buzzerBootBlipPhase  = 0;
            silenceBuzzer();
        }
        Serial.printf("[BUZZER] %s\n", g_buzzerEnabled ? "enabled" : "disabled");
        return;
    }

    // ---- Ping (connectivity test) ----

    if (strcmp(cmd, "ping") == 0) {
        String reply = String("{\"cmd\":\"pong\",\"id\":\"") + g_robotId + "\"}";
        ws.send(reply);
        Serial.println("[PING] pong sent");
        return;
    }

    // ---- Pre-game countdown ----

    if (strcmp(cmd, "countdown_tick") == 0) {
        int count = doc["count"] | 1;
        int total = doc["total"] | 5;

        leds.setCountdownTick(count, total);

        // All bloops the same low pitch; last tick (count=1) jumps to a higher "beeep"
        uint32_t baseFreq   = (count == 1) ? 350u : 110u;
        uint8_t  targetDuty = (count == 1) ? 128u :  90u; // last is slightly louder
        startBuzzerCountdownTick(baseFreq, targetDuty);
        Serial.printf("[COUNTDOWN] tick count=%d/%d freq=%u duty=%u\n", count, total, baseFreq, targetDuty);
        return;
    }

    if (strcmp(cmd, "set_player_color") == 0) {
        uint8_t r = (uint8_t)(int)(doc["r"] | 0);
        uint8_t g = (uint8_t)(int)(doc["g"] | 0);
        uint8_t b = (uint8_t)(int)(doc["b"] | 0);
        leds.setPlayerColor(r, g, b);
        Serial.printf("[LED] set_player_color r=%d g=%d b=%d\n", r, g, b);
        return;
    }

    if (strcmp(cmd, "clear_player_color") == 0) {
        leds.clearPlayerColor();
        Serial.println("[LED] clear_player_color");
        return;
    }

    if (strcmp(cmd, "game_start_fanfare") == 0) {
        leds.clearPlayerColor();
        // Restore full HP display (game starts at full HP)
        leds.setHp(100, 100);
        startBuzzerFanfareEffect();
        Serial.println("[COUNTDOWN] game_start_fanfare");
        return;
    }

    if (strcmp(cmd, "reset_idle") == 0) {
        leds.resetToIdle();
        Serial.println("[LED] reset_idle — bounce mode");
        return;
    }

}

// ---- WebSocket — connect (blocking until handshake completes or fails) ----
// This is only called after we receive a UDP reply, so the server is known to
// be reachable.  A failed connect clears g_wsUrl so discovery restarts.
static void connectWebSocket()
{
    if (g_wsUrl.isEmpty()) return;

    ws.close();
    delay(100);

    ws.onMessage([](WebsocketsMessage msg) {
        if (msg.isText()) handleWsText(msg.data());
    });

    ws.onEvent([](WebsocketsEvent e, String data) {
        if (e == WebsocketsEvent::ConnectionOpened) {
            g_wsOpen        = true;
            g_lastHeartbeat = 0; // trigger immediate heartbeat next tick

            // hello includes IP, name, and config so the server can sync its UI
            String hello = String("{\"cmd\":\"hello\",\"id\":\"") + g_robotId +
                           "\",\"name\":\""      + g_robotName +
                           "\",\"ip\":\""        + WiFi.localIP().toString() +
                           "\",\"hflip\":"       + String(g_hflip) +
                           ",\"vflip\":"         + String(g_vflip) +
                           ",\"inv_throttle\":"  + String(g_inv_throttle) +
                           ",\"inv_steer\":"     + String(g_inv_steer) +
                           ",\"inv_turret\":"    + String(g_inv_turret) +
                           ",\"fps\":"           + String(g_videoFps) +
                           ",\"fsize\":"         + String(g_videoFrameSizeIdx) +
                           ",\"fquality\":"      + String(g_videoQuality) + "}";
            ws.send(hello);
            leds.setStatus(StatusPattern::ConnectedSlow);
            leds.setBootPhase(BootPhase::ServerDone);
            startBuzzerBootBlip(1100); // B♭5 blip-blip — server connected
            Serial.println("[WS] opened");

        } else if (e == WebsocketsEvent::ConnectionClosed) {
            if (data.length() > 0)
                Serial.printf("[WS] closed reason: %s\n", data.c_str());
            onWsClose();
        }
    });

    // Use three-arg connect to avoid URL-parsing issues in the library.
    // g_wsHost / g_wsPort / g_wsPath are parsed in readDiscoveryReply().
    Serial.printf("[WS] connecting %s:%d%s\n",
                  g_wsHost.c_str(), g_wsPort, g_wsPath.c_str());
    if (!ws.connect(g_wsHost.c_str(), g_wsPort, g_wsPath.c_str())) {
        Serial.println("[WS] connect failed");
        g_wsUrl.clear(); // back to discovery
    }
}

// ============================================================
// setup
// ============================================================
void setup()
{
    // Buzzer pin first — GPIO1 is also UART0 TX on some configurations, which
    // idles HIGH and would drive the transistor on.  Assert LOW immediately
    // before Serial.begin() can touch the pin.
    pinMode(BUZZER_PIN, OUTPUT);
    digitalWrite(BUZZER_PIN, LOW);

    Serial.begin(115200);
    delay(100);
    Serial.println("[BOOT] Thundergeddon ESP32-S3");

    // LEDs and status pin
    leds.begin();
    leds.setStatus(StatusPattern::SearchingFast);
    leds.update(millis());

    g_robotId = makeRobotId();
    Serial.print("[ID] "); Serial.println(g_robotId);

    // Motor controller initialises I2C and configures PCA9555
    motors.begin(I2C_SDA, I2C_SCL);
    motors.enable(false); // SLEEP asserted; drivers off

    // IrController stores a reference to motors for shared I2C access
    ir.begin(motors);

    // PCA9555 INT (GPIO14): attach here (same TU as g_pca9555IntFired) so the
    // linker places the IRAM literal pool adjacent to the ISR body.
    // open-drain output with external pull-up; fires FALLING when any Port 0 input changes.
    pinMode(14, INPUT);
    attachInterrupt(digitalPinToInterrupt(14),
        []() IRAM_ATTR { g_pca9555IntFired = true; },
        FALLING);

    // I2C bus scan — helps diagnose RFID address
    Serial.println("[I2C] Scanning bus...");
    int found = 0;
    for (uint8_t addr = 1; addr < 127; addr++) {
        Wire.beginTransmission(addr);
        if (Wire.endTransmission() == 0) {
            Serial.printf("[I2C]   device at 0x%02X\n", addr);
            found++;
        }
    }
    if (found == 0) Serial.println("[I2C]   nothing found");

    // RFID uses the same Wire bus (already started by motors.begin)
    rfid.begin();

    // Camera: configure pins only; driver started lazily on stream_on
    cam.begin();

    // Load flip/mirror prefs from NVS (defaults match original hardcoded values)
    {
        Preferences prefs;
        prefs.begin("tg_cam", true); // read-only
        g_hflip = prefs.getInt("hflip", 1);
        g_vflip = prefs.getInt("vflip", 1);
        g_videoFps          = prefs.getInt("fps",      20);
        g_videoFrameSizeIdx = prefs.getInt("fsize",    2);
        g_videoQuality      = prefs.getInt("fquality", 10);
        prefs.end();
        cam.applyFlip(g_vflip != 0, g_hflip != 0);
        cam.setFrameSize(videoIdxToFrameSize(g_videoFrameSizeIdx));
        cam.setQuality(g_videoQuality);
        mjpeg.setMaxFps(g_videoFps);
        Serial.printf("[CAM] flip loaded hflip=%d vflip=%d; video fps=%d fsize=%d quality=%d\n",
                      g_hflip, g_vflip, g_videoFps, g_videoFrameSizeIdx, g_videoQuality);
    }

    // Load drive inversion prefs from NVS
    {
        Preferences prefs;
        prefs.begin("tg_drv", true);
        g_inv_throttle = prefs.getInt("inv_throttle", 0);
        g_inv_steer    = prefs.getInt("inv_steer",    0);
        g_inv_turret   = prefs.getInt("inv_turret",   0);
        prefs.end();
        Serial.printf("[DRV] config loaded inv_throttle=%d inv_steer=%d inv_turret=%d\n",
                      g_inv_throttle, g_inv_steer, g_inv_turret);
    }

    // Load robot name from NVS (empty string = no name set yet)
    {
        Preferences prefs;
        prefs.begin("tg_id", true);
        g_robotName = prefs.getString("name", "");
        prefs.end();
        if (g_robotName.isEmpty())
            Serial.println("[ID] No saved name (will use ID as default)");
        else
            Serial.printf("[ID] Loaded name: %s\n", g_robotName.c_str());
    }

    // Hardware init complete — solid red on LEDs 0,1
    leds.setBootPhase(BootPhase::HwDone);
    playBuzzerBootBlip(440); // A4 blip-blip — hardware ready

    // Wi-Fi: blocking until connected — flashing green on LEDs 2,3
    leds.setBootPhase(BootPhase::WifiConnecting);
    connectWifi();
    leds.setBootPhase(BootPhase::WifiDone);
    playBuzzerBootBlip(700); // F5 blip-blip — Wi-Fi connected

    // MJPEG server binds port 81 now; streaming gate (_enabled) stays false
    // until the server sends stream_on
    mjpeg.begin();

    // OTA: pause callback puts everything in a safe state before flashing
    String otaHost = String("thunder-") + g_robotId;
    OtaSupport::begin(
        otaHost.c_str(),
        "thunder123",
        []() {
            // Pause: stop everything that could cause problems during OTA.
            // mjpeg.stop() blocks until the HTTP server task exits, so it is
            // safe to call cam.stop() immediately after with no race condition.
            // Closing the WebSocket removes competing TCP buffer traffic on the
            // Wi-Fi radio during the OTA transfer.
            motors.enable(false);
            ir.stopEmit();
            mjpeg.stop();
            if (cam.isStarted()) cam.stop();
            ws.close();
            g_wsOpen = false;
            g_wsUrl.clear();
            // Start chirp: two tiny ascending pips
            ledcSetup(BUZZER_LEDC_CH, 1200, 8); ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
            ledcWrite(BUZZER_LEDC_CH, 28); delay(18);
            ledcWrite(BUZZER_LEDC_CH, 0);  delay(12);
            ledcSetup(BUZZER_LEDC_CH, 2400, 8);
            ledcWrite(BUZZER_LEDC_CH, 28); delay(18);
            ledcWrite(BUZZER_LEDC_CH, 0);
            leds.showOtaProgress(0);
        },
        nullptr,       // no resume needed (device reboots after OTA)
        otaProgressCb,
        otaEndCb
    );

    // Entering server discovery — flashing blue on LEDs 4,5
    leds.setBootPhase(BootPhase::ServerConnecting);
    startDiscovery();
}

// ============================================================
// loop
// ============================================================
void loop()
{
    const uint32_t now = millis();

    // Skip normal work if OTA is in progress
    if (OtaSupport::active) {
        OtaSupport::handle();
        return;
    }

    OtaSupport::handle();

    // ---- Motor soft-PWM tick (every 1 ms) ----
    if (now - g_lastMotorTick >= MOTOR_TICK_MS) {
        g_lastMotorTick = now;
        motors.tick();
    }

    // ---- Drive watchdog (safety-stop on silent Wi-Fi drop) ----
    // The phone sends drive at 20 Hz even with stick at zero, so >500 ms silence
    // means the connection is genuinely dead — coast targets to zero without
    // disabling the SLEEP line (motors recover instantly when commands resume).
    if (g_lastDriveTime > 0 && (now - g_lastDriveTime) > DRIVE_WATCHDOG_MS) {
        motors.setLeftRight(0, 0);
        g_lastDriveTime = 0;
        Serial.println("[WD] drive watchdog fired — coasting to stop");
    }

    // ---- LED effects and status blink ----
    leds.update(now);

    // ---- Buzzer ----
    updateBuzzer(now);
    updateBuzzerCountdownEffect(now);
    updateBuzzerFireEffect(now);
    updateBuzzerHitEffect(now);
    updateBuzzerRearHitEffect(now);
    updateBuzzerHealEffect(now);
    updateBuzzerDeathEffect(now);
    updateBuzzerCaptureEffect(now);
    updateBuzzerFanfareEffect(now);
    updateBuzzerBootBlip(now);

    // ---- RFID tag scan ----
    {
        String uid = rfid.poll(now);
        if (uid.length() > 0 && g_wsOpen)
        {
            String msg = String("{\"cmd\":\"rfid\",\"uid\":\"") + uid + "\"}";
            ws.send(msg);
        }
    }

    // ---- IR: handshake emit burst (beginEmitLeft/Right autonomous 3ms/3ms cycling) ----
    ir.updateEmitBurst(now);

    // ---- IR: handshake listen window (ir_listen_window command) ----
    ir.updateWindow(now);
    if (ir.isWindowDone()) {
        uint8_t mask = ir.takeWindowMask();
        if (g_wsOpen) {
            String resp = String("{\"cmd\":\"ir_window_result\",\"mask\":") +
                          String(mask) + "}";
            ws.send(resp);
            Serial.printf("[IR] window result mask=0x%02X\n", mask);
        }
    }

    // ---- Wi-Fi health check ----
    if (now - g_lastWifiCheck >= WIFI_CHECK_MS) {
        g_lastWifiCheck = now;
        if (WiFi.status() != WL_CONNECTED) {
            Serial.println("[WIFI] dropped, reconnecting...");
            WiFi.reconnect();
            leds.setStatus(StatusPattern::SearchingFast);
            // Also close the WebSocket cleanly if it was open
            if (g_wsOpen) onWsClose();
        }
    }

    // ---- Discovery loop (no server URL yet) ----
    if (g_wsUrl.isEmpty()) {
        maybeAnnounce();
        if (readDiscoveryReply()) {
            connectWebSocket();
        }
        yield();
        return;
    }

    // ---- WebSocket ----
    ws.poll();

    if (g_wsOpen) {
        // Periodic heartbeat
        if (now - g_lastHeartbeat >= HEARTBEAT_MS) {
            g_lastHeartbeat = now;
            String hb = String("{\"cmd\":\"hb\",\"t\":") + String(now) + "}";
            ws.send(hb);
        }
    } else {
        // URL is set but socket is not open — connection died without a close event
        // (e.g. server hard-rebooted).  Clean up and rediscover.
        onWsClose();
    }

    yield(); // feed watchdog; allow background tasks (MJPEG, Wi-Fi) to run
}
