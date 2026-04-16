// OtaSupport.h — ArduinoOTA wrapper with pause/resume callbacks.
// Hostname format: thunder-<MAC12>  (e.g. thunder-A1B2C3D4E5F6)
// Password: thunder123
// Port: 3232 (default ArduinoOTA port)

#pragma once
#include <Arduino.h>
#include <ArduinoOTA.h>
#include <WiFi.h>

namespace OtaSupport
{
    // Set true by the onStart callback so loop() can skip normal work during update.
    inline volatile bool active = false;

    using PauseFn  = void (*)();
    using ResumeFn = void (*)();

    inline PauseFn  onPause  = nullptr;
    inline ResumeFn onResume = nullptr;

    inline void begin(const char* hostname,
                      const char* password = nullptr,
                      PauseFn  pauseFn  = nullptr,
                      ResumeFn resumeFn = nullptr)
    {
        onPause  = pauseFn;
        onResume = resumeFn;

        WiFi.setSleep(false);                    // keep radio fully awake during transfers
        WiFi.setTxPower(WIFI_POWER_19_5dBm);

        if (hostname && *hostname) ArduinoOTA.setHostname(hostname);
        ArduinoOTA.setPort(3232);
        if (password && *password) ArduinoOTA.setPassword(password);

        ArduinoOTA.onStart([]() {
            active = true;
            if (onPause) onPause();
            Serial.println("[OTA] start");
        });

        ArduinoOTA.onEnd([]() {
            Serial.println("[OTA] end");
            // Device reboots after this; onResume is not needed
        });

        ArduinoOTA.onProgress([](unsigned int done, unsigned int total) {
            static uint8_t lastPct = 255;
            uint8_t pct = total ? (uint8_t)(done * 100u / total) : 0;
            if (pct != lastPct) {
                lastPct = pct;
                Serial.printf("[OTA] %u%%\n", pct);
            }
            yield(); // keep Wi-Fi stack fed during long transfers
        });

        ArduinoOTA.onError([](ota_error_t e) {
            Serial.printf("[OTA] error %u\n", (unsigned)e);
            active = false;
            if (onResume) onResume();
        });

        ArduinoOTA.begin();
        Serial.println("[OTA] ready on port 3232");
    }

    inline void handle() { ArduinoOTA.handle(); }
}
