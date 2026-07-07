// Capture Point Beacon firmware.
//
// Standalone WS2812 light fixture for a physical capture point (North/Centre/
// South). Connects to the same Unity server infrastructure as the tank
// robots (UDP discovery on 30560, WebSocket to the beacon endpoint), but
// carries none of the robot's camera/motor/IR/RFID hardware.
//
// Boot sequence: connect Wi-Fi -> UDP-discover Unity's beacon WebSocket URL
// -> connect -> send hello -> sit in Idle (bouncing white) until Unity tells
// it to go Unlit (match started) or Captured (a team took the point).

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <ArduinoWebsockets.h>
#include <ArduinoJson.h>

#include "secrets.h"
#include "LedController.h"
#include "OtaSupport.h"

#ifndef POINT_INDEX
#define POINT_INDEX -1
#endif
#ifndef POINT_NAME
#define POINT_NAME "Unknown"
#endif

using namespace websockets;

static constexpr uint16_t DISCOVERY_PORT = 30560;
static constexpr uint32_t ANNOUNCE_MS    = 2000;
static constexpr uint32_t HEARTBEAT_MS   = 2000;
static constexpr uint32_t WIFI_CHECK_MS  = 5000;

static LedController leds;
static WiFiUDP       udp;
static WebsocketsClient ws;

static String g_beaconId;

static String   g_wsUrl, g_wsHost, g_wsPath;
static uint16_t g_wsPort = 0;
static bool     g_wsOpen = false;

static uint32_t g_lastAnnounce  = 0;
static uint32_t g_lastHeartbeat = 0;
static uint32_t g_lastWifiCheck = 0;

// ---- Beacon ID (12-char uppercase MAC hex, no separators) ----
static String makeBeaconId()
{
    uint64_t mac = ESP.getEfuseMac();
    char buf[13];
    snprintf(buf, sizeof(buf), "%02X%02X%02X%02X%02X%02X",
             (uint8_t)(mac >> 40), (uint8_t)(mac >> 32),
             (uint8_t)(mac >> 24), (uint8_t)(mac >> 16),
             (uint8_t)(mac >>  8), (uint8_t)(mac >>  0));
    return String(buf);
}

static void connectWifi()
{
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false);

    const int NET_COUNT = sizeof(WIFI_NETWORKS) / sizeof(WIFI_NETWORKS[0]);
    const unsigned long ATTEMPT_MS = 15000;

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

    String payload = String("{\"robotId\":\"") + g_beaconId +
                     "\",\"callsign\":\"\",\"ip\":\"" + WiFi.localIP().toString() +
                     "\",\"kind\":\"beacon\",\"point\":" + String(POINT_INDEX) + "}";
    udp.beginPacket(IPAddress(255, 255, 255, 255), DISCOVERY_PORT);
    udp.write((const uint8_t*)payload.c_str(), payload.length());
    udp.endPacket();
    Serial.println("[DISCOVERY] announce");
}

static bool readDiscoveryReply()
{
    int sz = udp.parsePacket();
    if (sz <= 0) return false;

    char buf[256];
    int n = udp.read(buf, sizeof(buf) - 1);
    if (n <= 0) return false;
    buf[n] = '\0';

    String s(buf);
    int k  = s.indexOf("\"ws\"");  if (k  < 0) return false;
    int c  = s.indexOf(':', k);    if (c  < 0) return false;
    int q1 = s.indexOf('"', c+1);  if (q1 < 0) return false;
    int q2 = s.indexOf('"', q1+1); if (q2 < 0) return false;

    g_wsUrl = s.substring(q1+1, q2);
    Serial.print("[DISCOVERY] ws="); Serial.println(g_wsUrl);

    int hStart = g_wsUrl.indexOf("://");
    hStart = (hStart >= 0) ? hStart + 3 : 0;
    int colon = g_wsUrl.indexOf(':', hStart);
    int slash = g_wsUrl.indexOf('/', hStart);
    if (colon > 0 && slash > colon) {
        g_wsHost = g_wsUrl.substring(hStart, colon);
        g_wsPort = g_wsUrl.substring(colon + 1, slash).toInt();
        g_wsPath = g_wsUrl.substring(slash);
    }
    Serial.printf("[DISCOVERY] host=%s port=%d path=%s\n",
                  g_wsHost.c_str(), g_wsPort, g_wsPath.c_str());

    return true;
}

static void onWsClose()
{
    g_wsOpen = false;
    g_wsUrl.clear();
    leds.setState(BeaconState::BootServer);
    startDiscovery();
}

static void handleWsText(const String& s)
{
    Serial.print("[WS] rx: "); Serial.println(s);

    JsonDocument doc;
    if (deserializeJson(doc, s) != DeserializationError::Ok) {
        Serial.println("[WS] JSON parse error");
        return;
    }

    const char* cmd = doc["cmd"] | "";

    if (strcmp(cmd, "set_color") == 0) {
        // Instant, no animation — used for hello-resync and "unlit" at match start.
        uint8_t r = doc["r"] | 0;
        uint8_t g = doc["g"] | 0;
        uint8_t b = doc["b"] | 0;
        if (r == 0 && g == 0 && b == 0) leds.setState(BeaconState::Unlit);
        else                            leds.setCaptured(r, g, b);
    } else if (strcmp(cmd, "capture_ripple") == 0) {
        // ~1s animated transition for a live capture: white ripples closing
        // in from both ends, settling into the capture colour.
        uint8_t r = doc["r"] | 0;
        uint8_t g = doc["g"] | 0;
        uint8_t b = doc["b"] | 0;
        leds.startCaptureRipple(r, g, b);
    } else if (strcmp(cmd, "vp_ripple") == 0) {
        // Brief single white ripple, mirrors the spectator display's score-tick flash.
        leds.startVpRipple();
    } else if (strcmp(cmd, "beacon_idle") == 0) {
        leds.setState(BeaconState::Idle);
    }
}

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
            g_lastHeartbeat = 0;

            String hello = String("{\"cmd\":\"hello\",\"id\":\"") + g_beaconId +
                           "\",\"point\":" + String(POINT_INDEX) +
                           ",\"name\":\"" + POINT_NAME +
                           "\",\"ip\":\"" + WiFi.localIP().toString() + "\"}";
            ws.send(hello);
            leds.setState(BeaconState::Idle);
            Serial.println("[WS] opened");

        } else if (e == WebsocketsEvent::ConnectionClosed) {
            if (data.length() > 0)
                Serial.printf("[WS] closed reason: %s\n", data.c_str());
            onWsClose();
        }
    });

    Serial.printf("[WS] connecting %s:%d%s\n",
                  g_wsHost.c_str(), g_wsPort, g_wsPath.c_str());
    if (!ws.connect(g_wsHost.c_str(), g_wsPort, g_wsPath.c_str())) {
        Serial.println("[WS] connect failed");
        g_wsUrl.clear();
    }
}

// ---- OTA callbacks ----
static void otaProgressCb(uint8_t pct) { leds.showOtaProgress(pct); }

static void otaPauseCb()
{
    ws.close();
    g_wsOpen = false;
    g_wsUrl.clear();
    leds.showOtaProgress(0);
}

void setup()
{
    Serial.begin(115200);
    delay(200);
    Serial.printf("[BOOT] Capture Point Beacon — point=%d (%s)\n", POINT_INDEX, POINT_NAME);

    leds.begin(); // BootHw

    g_beaconId = makeBeaconId();
    Serial.printf("[BOOT] id=%s\n", g_beaconId.c_str());

    leds.setState(BeaconState::BootWifi);
    connectWifi();

    leds.setState(BeaconState::BootServer);

    String otaHost = String("beacon-") + g_beaconId;
    OtaSupport::begin(otaHost.c_str(), OTA_PASSWORD, otaPauseCb, nullptr, otaProgressCb, nullptr);

    startDiscovery();
}

void loop()
{
    const uint32_t now = millis();

    if (OtaSupport::active) {
        OtaSupport::handle();
        return;
    }
    OtaSupport::handle();

    leds.update(now);

    if (now - g_lastWifiCheck >= WIFI_CHECK_MS) {
        g_lastWifiCheck = now;
        if (WiFi.status() != WL_CONNECTED) {
            Serial.println("[WIFI] dropped, reconnecting...");
            WiFi.reconnect();
            leds.setState(BeaconState::BootWifi);
            if (g_wsOpen) onWsClose();
        }
    }

    if (g_wsUrl.isEmpty()) {
        maybeAnnounce();
        if (readDiscoveryReply()) connectWebSocket();
        yield();
        return;
    }

    ws.poll();

    if (g_wsOpen) {
        if (now - g_lastHeartbeat >= HEARTBEAT_MS) {
            g_lastHeartbeat = now;
            ws.send(String("{\"cmd\":\"hb\",\"t\":") + String(now) + "}");
        }
    } else {
        onWsClose();
    }

    yield();
}
