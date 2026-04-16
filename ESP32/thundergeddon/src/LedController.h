// LedController.h — WS2812B strip (6 LEDs, GPIO38) + status LED (GPIO48).
//
// Strip effects (in priority order, highest first):
//   flashHit(ms)   — all red for ms, then restore HP bar
//   flashFire(ms)  — all white for ms, then restore HP bar
//   HP bar         — blue pixels proportional to remaining HP (default)
//
// A hit arriving during a fire flash immediately overrides it.
// After any timed effect expires, the HP bar is automatically redrawn.
//
// Status LED (GPIO48) is a plain active-LOW green LED.  Pattern is set by
// the main loop to reflect connection state:
//   SearchingFast   — 100 ms blink (no server found)
//   ConnectedSlow   — 500 ms blink (connected, not in game)
//   InGameSolid     — continuously on (game running)

#pragma once
#include <Arduino.h>
#include <Adafruit_NeoPixel.h>

static constexpr int LED_STRIP_PIN   = 38;
static constexpr int LED_STRIP_COUNT = 6;
static constexpr int LED_STATUS_PIN  = 48; // active-LOW plain green LED

enum class StatusPattern : uint8_t {
    SearchingFast,  // fast blink — scanning for server
    ConnectedSlow,  // slow blink — websocket connected
    InGameSolid,    // solid on   — game in progress
};

class LedController
{
public:
    LedController()
      : _strip(LED_STRIP_COUNT, LED_STRIP_PIN, NEO_GRB + NEO_KHZ800)
    {}

    void begin()
    {
        _strip.begin();
        _strip.clear();
        _strip.show();

        pinMode(LED_STATUS_PIN, OUTPUT);
        _setStatusPin(false); // off at boot
    }

    // Must be called every loop tick to advance timed effects and blink patterns.
    void update(uint32_t now)
    {
        _updateEffect(now);
        _updateStatus(now);
    }

    // ---- HP bar ----

    // Update stored HP values and redraw the bar (unless an effect is active).
    void setHp(int hp, int maxHp)
    {
        _hp    = (hp    < 0)    ? 0    : hp;
        _maxHp = (maxHp < 1)    ? 1    : maxHp;
        if (_effect == Effect::None) _drawHpBar();
    }

    // ---- Timed flash effects ----

    // Fire sequence:
    //   Phase 1 (0–300 ms): ramp all 6 LEDs from dark to full white.
    //   Phase 2 (300–450 ms): extinguish LEDs one by one from index 5→0
    //                          (front of robot first), like a projectile launching.
    void fireSequence()
    {
        if (_effect == Effect::Hit) return; // hit takes priority
        _effect   = Effect::FireSeq;
        _seqStart = millis();
        _strip.clear();
        _strip.show();
    }

    // Legacy solid-white flash (kept for other callers).
    void flashFire(uint32_t durationMs = 200)
    {
        if (_effect == Effect::Hit) return;
        _effect    = Effect::Fire;
        _effectEnd = millis() + durationMs;
        _fillStrip(255, 255, 255);
    }

    // All-red flash: robot was hit.
    void flashHit(uint32_t durationMs = 300)
    {
        _effect    = Effect::Hit; // always overrides fire
        _effectEnd = millis() + durationMs;
        _fillStrip(255, 0, 0);
    }

    // ---- Status LED ----

    void setStatus(StatusPattern p)
    {
        if (_statusPattern == p) return;
        _statusPattern    = p;
        _statusNextToggle = 0; // force immediate update
    }

private:
    enum class Effect : uint8_t { None, Fire, Hit, FireSeq };

    // Check whether the active timed effect has expired; drive animations.
    void _updateEffect(uint32_t now)
    {
        if (_effect == Effect::None) return;

        if (_effect == Effect::FireSeq) {
            // Timing: 300 ms ease-in ramp | 100 ms gap (hold white) | 150 ms blast → 550 ms total
            uint32_t elapsed = now - _seqStart;
            if (elapsed >= 550) {
                _effect = Effect::None;
                _drawHpBar();
                return;
            }
            if (elapsed < 300) {
                // Phase 1: quadratic ease-in ramp from dark to full white.
                float t = (float)elapsed / 300.0f;
                uint8_t bright = (uint8_t)(t * t * 255.0f);
                for (int i = 0; i < LED_STRIP_COUNT; i++)
                    _strip.setPixelColor(i, _strip.Color(bright, bright, bright));
                _strip.show();
            } else if (elapsed >= 400) {
                // Phase 2 (after 100 ms gap): extinguish LEDs front-first (5→0), 25 ms each.
                int ledsOff = (int)((elapsed - 400u) / 25u);
                _strip.clear();
                int lit = LED_STRIP_COUNT - ledsOff;
                for (int i = 0; i < lit; i++)
                    _strip.setPixelColor(i, _strip.Color(255, 255, 255));
                _strip.show();
            }
            // Gap (300–400 ms): LEDs hold at full white from the last phase-1 write.
            return;
        }

        // Fire / Hit timed effects: wait for expiry then restore HP bar.
        if ((int32_t)(now - _effectEnd) >= 0) {
            _effect = Effect::None;
            _drawHpBar();
        }
    }

    // Advance the status LED blink pattern.
    void _updateStatus(uint32_t now)
    {
        switch (_statusPattern) {
        case StatusPattern::InGameSolid:
            _setStatusPin(true);
            break;
        case StatusPattern::SearchingFast:
            if ((int32_t)(now - _statusNextToggle) >= 0) {
                _statusLedOn      = !_statusLedOn;
                _setStatusPin(_statusLedOn);
                _statusNextToggle = now + 100; // 5 Hz
            }
            break;
        case StatusPattern::ConnectedSlow:
            if ((int32_t)(now - _statusNextToggle) >= 0) {
                _statusLedOn      = !_statusLedOn;
                _setStatusPin(_statusLedOn);
                _statusNextToggle = now + 500; // 1 Hz
            }
            break;
        }
    }

    // Draw blue HP bar.  Each of the 6 LEDs represents 1/6 of maxHp.
    // The rightmost lit LED may be partially dimmed for a smoother reading.
    void _drawHpBar()
    {
        _strip.clear();
        if (_maxHp > 0 && _hp > 0) {
            float frac = (float)_hp / (float)_maxHp;
            float litf = frac * LED_STRIP_COUNT;
            int   full = (int)litf;
            int   part = (int)((litf - (float)full) * 255.0f);

            for (int i = 0; i < full && i < LED_STRIP_COUNT; i++) {
                _strip.setPixelColor(i, _strip.Color(0, 0, 255));
            }
            if (full < LED_STRIP_COUNT && part > 8) { // ignore sub-pixel rounding noise
                _strip.setPixelColor(full, _strip.Color(0, 0, (uint8_t)part));
            }
        }
        _strip.show();
    }

    void _fillStrip(uint8_t r, uint8_t g, uint8_t b)
    {
        for (int i = 0; i < LED_STRIP_COUNT; i++) {
            _strip.setPixelColor(i, _strip.Color(r, g, b));
        }
        _strip.show();
    }

    // GPIO48 is active-LOW (LED cathode → GPIO, anode → 3V3 via 1 kΩ resistor).
    void _setStatusPin(bool on)
    {
        digitalWrite(LED_STATUS_PIN, on ? LOW : HIGH);
    }

    Adafruit_NeoPixel _strip;
    Effect            _effect            = Effect::None;
    uint32_t          _effectEnd         = 0;
    uint32_t          _seqStart          = 0; // millis() when FireSeq began
    int               _hp                = 0;
    int               _maxHp             = 100;

    StatusPattern     _statusPattern     = StatusPattern::SearchingFast;
    uint32_t          _statusNextToggle  = 0;
    bool              _statusLedOn       = false;
};
