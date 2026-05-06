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

// Offset between Unity clock (ms) and local millis(). Set by time_sync command.
// To convert a Unity timestamp to local millis: localMs = unityMs - g_unityTimeOffset
static int32_t  g_unityTimeOffset = 0;
// Edge-detect for fire slot completion (to re-enable motors after slot ends)
static bool     g_fireSlotActive  = false;

static uint32_t g_lastAnnounce   = 0;
static uint32_t g_lastHeartbeat  = 0;
static uint32_t g_lastMotorTick  = 0;
static uint32_t g_lastWifiCheck  = 0;

// ---- Buzzer (simple timed tone) ----
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
// Phase 1 (0–300 ms):   quadratic ease-in sweep 300→3000 Hz, volume 0→128.
// Phase 2 (300–400 ms): gap — buzzer silent, LEDs hold white.
// Phase 3 (400–900 ms): static that fades out; centre frequency slides 8000→150 Hz
//                        so the blast sounds like it's leaving the tank.
static uint8_t  g_buzzerFirePhase     = 0; // 0=off, 1=ramp, 2=gap, 3=blast
static uint32_t g_buzzerFireStart     = 0;
static uint32_t g_buzzerFireNextNoise = 0;
static uint32_t g_buzzerFireLastUp    = 0;

static void startBuzzerFireEffect()
{
    g_buzzerActive = false; // cancel any plain tone
    ledcSetup(BUZZER_LEDC_CH, 300, 8);
    ledcAttachPin(BUZZER_PIN, BUZZER_LEDC_CH);
    ledcWrite(BUZZER_LEDC_CH, 0);
    g_buzzerFirePhase  = 1;
    g_buzzerFireStart  = millis();
    g_buzzerFireLastUp = millis();
}

static void updateBuzzerFireEffect(uint32_t now)
{
    if (g_buzzerFirePhase == 0) return;

    uint32_t elapsed = now - g_buzzerFireStart;

    // ---- Phase 1: ease-in sweep ----
    if (g_buzzerFirePhase == 1) {
        if (elapsed >= 300) {
            ledcWrite(BUZZER_LEDC_CH, 0); // silence on transition
            g_buzzerFirePhase = 2;
            return;
        }
        if ((int32_t)(now - g_buzzerFireLastUp) >= 10) {
            g_buzzerFireLastUp = now;
            float t  = (float)elapsed / 300.0f;
            float t2 = t * t; // quadratic ease-in
            uint32_t freq = 300u + (uint32_t)(t2 * 2700.0f);
            uint8_t  duty = (uint8_t)(t2 * 128.0f);
            ledcSetup(BUZZER_LEDC_CH, freq, 8);
            ledcWrite(BUZZER_LEDC_CH, duty);
        }
        return;
    }

    // ---- Phase 2: 100 ms gap (silent) ----
    if (g_buzzerFirePhase == 2) {
        if (elapsed >= 400) {
            g_buzzerFirePhase     = 3;
            g_buzzerFireNextNoise = now;
        }
        return;
    }

    // ---- Phase 3: blocky static fading out with descending pitch (500 ms) ----
    if (g_buzzerFirePhase == 3) {
        uint32_t ph3 = elapsed - 400u;
        if (ph3 >= 500u) {
            silenceBuzzer();
            g_buzzerFirePhase = 0;
            return;
        }
        if ((int32_t)(now - g_buzzerFireNextNoise) >= 0) {
            float t = (float)ph3 / 500.0f; // 0 → 1 over 500 ms

            // Centre frequency slides 8000 → 150 Hz — blast pitching down as it leaves.
            float centre = 8000.0f - t * 7850.0f;

            // Wide random spread ±100 % of centre for aggressive jumps.
            float spread = centre;
            int32_t rnd  = (int32_t)(((float)(random(2001) - 1000) / 1000.0f) * spread);
            int32_t raw  = (int32_t)centre + rnd;
            if (raw < 40)    raw = 40;
            if (raw > 20000) raw = 20000;
            uint32_t freq = (uint32_t)raw;

            // Linear fade-out from full to silent.
            uint8_t duty = (uint8_t)((1.0f - t) * 128.0f);

            ledcSetup(BUZZER_LEDC_CH, freq, 8);
            ledcWrite(BUZZER_LEDC_CH, duty);
            // Block length shrinks 20 ms → 1 ms as blast fades out.
            uint32_t blockMs = 1u + (uint32_t)((1.0f - t) * 19.0f);
            g_buzzerFireNextNoise = now + blockMs;
        }
    }
}

// ---- Buzzer hit effect ----
// Phase 1 (0–50 ms):   clean sharp strike at 1200 Hz, full volume.
// Phase 2 (50–380 ms): pixelated falling noise — centre descends 1200→150 Hz,
//                       ±50 % random spread, 8 ms blocks, volume fades 128→0.
static uint8_t  g_buzzerHitPhase     = 0; // 0=off, 1=strike, 2=fall
static uint32_t g_buzzerHitStart     = 0;
static uint32_t g_buzzerHitNextBlock = 0;

static void startBuzzerHitEffect()
{
    g_buzzerActive    = false; // cancel plain tone
    g_buzzerFirePhase = 0;     // cancel fire effect
    ledcSetup(BUZZER_LEDC_CH, 1200, 8);
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
            float t = (float)ph2 / 330.0f; // 0→1
            float centre = 1200.0f - t * 1050.0f; // 1200→150 Hz
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
    Serial.print("[WIFI] connecting to ");
    Serial.println(WIFI_SSID);
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false);           // low-latency radio; important for drive commands
    WiFi.begin(WIFI_SSID, WIFI_PASS);
    while (WiFi.status() != WL_CONNECTED) {
        Serial.print(".");
        delay(300);
    }
    Serial.println();
    Serial.print("[WIFI] IP=");
    Serial.println(WiFi.localIP());
}

// ---- UDP discovery ----
static void startDiscovery()
{
    udp.stop();
    if (!udp.begin(DISCOVERY_PORT)) {
        Serial.println("[DISCOVERY] bind failed");
        return;
    }
    g_lastAnnounce = 0; // force immediate announce on next loop tick
    Serial.println("[DISCOVERY] listening");
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
    motors.enable(false);
    ir.stopEmit();
    mjpeg.setEnabled(false);
    if (cam.isStarted()) cam.stop();
    leds.setStatus(StatusPattern::SearchingFast);
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
        motors.setLeftRight(r, l); // motors swapped after hardware change
        return;
    }

    if (strcmp(cmd, "turret") == 0) {
        float v = doc["speed"] | 0.0f;
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

    // ---- Game effects ----

    if (strcmp(cmd, "flash_fire") == 0) {
        leds.fireSequence();
        startBuzzerFireEffect();
        return;
    }

    if (strcmp(cmd, "flash_hit") == 0) {
        leds.flashHit();
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

    if (strcmp(cmd, "ir_emit_prepare") == 0) {
        motors.enable(false); // motors off while emitting to reduce electrical noise
        ir.startEmit();
        ws.send("{\"cmd\":\"ir_emit_ready\"}");
        Serial.println("[IR] emit_prepare → ir_emit_ready sent");
        return;
    }

    if (strcmp(cmd, "ir_emit_stop") == 0) {
        ir.stopEmit();
        motors.enable(true);
        Serial.println("[IR] emit_stop");
        return;
    }

    // ---- Ping (connectivity test) ----

    if (strcmp(cmd, "ping") == 0) {
        String reply = String("{\"cmd\":\"pong\",\"id\":\"") + g_robotId + "\"}";
        ws.send(reply);
        Serial.println("[PING] pong sent");
        return;
    }

    // ---- IR listen ----

    if (strcmp(cmd, "ir_listen_and_report") == 0) {
        int ms = doc["ms"] | 100;
        ir.startListen((uint32_t)(ms > 0 ? ms : 100));
        return;
    }

    // ---- Clock sync ----
    if (strcmp(cmd, "time_sync") == 0) {
        int32_t ut = (int32_t)(doc["ut"] | 0);
        g_unityTimeOffset = ut - (int32_t)millis();
        Serial.printf("[IR] time_sync ut=%d offset=%d\n", ut, g_unityTimeOffset);
        return;
    }

    // ---- IR fire slot (this robot is the shooter) ----
    if (strcmp(cmd, "ir_fire_slot") == 0) {
        int     slotId      = doc["slot_id"]   | 0;
        int32_t slotStartUt = (int32_t)(doc["slot_start"] | 0);
        int     b1Dur       = doc["b1_dur"]    | 10;
        int     gap12       = doc["b1_b2_gap"] | 25;
        int     b2Dur       = doc["b2_dur"]    | 10;
        int     repGap      = doc["rep_gap"]   | 25;
        int     reps        = doc["reps"]      | 2;
        int32_t slotStartLocal = slotStartUt - g_unityTimeOffset;
        ir.scheduleFireSlot(slotId, slotStartLocal, b1Dur, gap12, b2Dur, repGap, reps);
        Serial.printf("[IR] ir_fire_slot slot=%d localStart=%d\n", slotId, slotStartLocal);
        return;
    }

    // ---- IR listen slot (this robot is a listener) ----
    if (strcmp(cmd, "ir_listen_slot") == 0) {
        int     slotId      = doc["slot_id"]   | 0;
        int32_t slotStartUt = (int32_t)(doc["slot_start"] | 0);
        int     b1Dur       = doc["b1_dur"]    | 10;
        int     gap12       = doc["b1_b2_gap"] | 25;
        int     b2Dur       = doc["b2_dur"]    | 10;
        int     repGap      = doc["rep_gap"]   | 25;
        int     reps        = doc["reps"]      | 2;
        int32_t slotStartLocal = slotStartUt - g_unityTimeOffset;
        ir.scheduleListenSlot(slotId, slotStartLocal, b1Dur, gap12, b2Dur, repGap, reps);
        Serial.printf("[IR] ir_listen_slot slot=%d localStart=%d\n", slotId, slotStartLocal);
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

            // hello includes IP so the server can show it in the robot list
            String hello = String("{\"cmd\":\"hello\",\"id\":\"") + g_robotId +
                           "\",\"ip\":\"" + WiFi.localIP().toString() + "\"}";
            ws.send(hello);
            leds.setStatus(StatusPattern::ConnectedSlow);
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

    // Wi-Fi: blocking until connected
    connectWifi();

    // MJPEG server binds port 81 now; streaming gate (_enabled) stays false
    // until the server sends stream_on
    mjpeg.begin();

    // OTA: pause callback puts everything in a safe state before flashing
    String otaHost = String("thunder-") + g_robotId;
    OtaSupport::begin(
        otaHost.c_str(),
        "thunder123",
        []() {
            // Pause: stop everything that could cause problems during OTA
            motors.enable(false);
            ir.stopEmit();
            mjpeg.setEnabled(false);
            if (cam.isStarted()) cam.stop();
        },
        nullptr // no resume needed (device reboots after OTA)
    );

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

    // ---- LED effects and status blink ----
    leds.update(now);

    // ---- Buzzer ----
    updateBuzzer(now);
    updateBuzzerFireEffect(now);
    updateBuzzerHitEffect(now);
    updateBuzzerHealEffect(now);
    updateBuzzerDeathEffect(now);
    updateBuzzerCaptureEffect(now);

    // ---- IR: legacy listen window (ir_listen_and_report) ----
    ir.update(now);
    if (ir.isListenDone()) {
        IrResult res = ir.takeResult();
        if (g_wsOpen) {
            String resp = String("{\"cmd\":\"ir_result\",\"hit\":") +
                          (res.hit ? "1" : "0") +
                          ",\"dir\":\"" + res.dir + "\"}";
            ws.send(resp);
            Serial.print("[IR] legacy result: "); Serial.println(resp);
        }
    }

    // ---- RFID tag scan ----
    {
        String uid = rfid.poll(now);
        if (uid.length() > 0 && g_wsOpen)
        {
            String msg = String("{\"cmd\":\"rfid\",\"uid\":\"") + uid + "\"}";
            ws.send(msg);
        }
    }

    // ---- IR: fire slot (shooter bursts) ----
    ir.updateFire(now);
    {
        bool fireNowActive = ir.isEmitting();
        if (g_fireSlotActive && !fireNowActive) {
            motors.enable(true);
            Serial.println("[IR] fire slot complete, motors re-enabled");
        }
        g_fireSlotActive = fireNowActive;
    }

    // ---- IR: listen slot (non-shooter detection) ----
    ir.updateListen(now);
    if (ir.isSlotDone()) {
        IrSlotResult sr = ir.takeSlotResult();
        if (g_wsOpen) {
            String resp = String("{\"cmd\":\"ir_slot_result\",\"slot_id\":") +
                          String(sr.slotId) +
                          ",\"b1\":" + String(sr.b1Mask) +
                          ",\"b2\":" + String(sr.b2Mask) + "}";
            ws.send(resp);
            Serial.print("[IR] slot result: "); Serial.println(resp);
        }
        motors.enable(true);
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
