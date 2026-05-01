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
static constexpr uint8_t DIM_BRIGHTNESS = 26; // ~10% of 255 — sustained effects only

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
        _drawHpBar(); // show initial full blue bar immediately
    }

    // Must be called every loop tick to advance timed effects and blink patterns.
    void update(uint32_t now)
    {
        _updateEffect(now);
        _updatePulse(now);
        _updateStatus(now);
    }

    // ---- HP bar ----

    // Update stored HP values and redraw the bar (unless an effect is active).
    // Resets the pulse cycle so the bar immediately shows the new state.
    void setHp(int hp, int maxHp)
    {
        _hp    = (hp    < 0) ? 0    : hp;
        _maxHp = (maxHp < 1) ? 1    : maxHp;
        _pulseOn         = true;
        _nextPulseToggle = millis() + 500;
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

    // Base heal: progressive green fill then rapid white flashes.
    // Phase 1 (0–750 ms): green bar charges from back (LED 0) to front (LED 5).
    // Phase 2 (750–1200 ms): 5 rapid white flashes (90 ms on / 90 ms off).
    void healCharge()
    {
        if (_effect == Effect::Hit) return; // hit takes priority
        _effect   = Effect::HealCharge;
        _seqStart = millis();
        _strip.clear();
        _strip.show();
    }

    // Death explosion: red/orange/yellow burst outward from center, then fire flicker
    // fading over 5 s, then auto-transition to alternating dim-red dead blink.
    void deathExplosion()
    {
        _effect   = Effect::DeathExplosion; // overrides everything including Hit
        _seqStart = millis();
        _deathFlickerNext = _seqStart + 20;
        _strip.clear();
        _strip.show();
    }

    // ---- Status LED ----

    void setStatus(StatusPattern p)
    {
        if (_statusPattern == p) return;
        _statusPattern    = p;
        _statusNextToggle = 0; // force immediate update
    }

private:
    enum class Effect : uint8_t { None, Fire, Hit, FireSeq, HealCharge, DeathExplosion, DeadBlink };

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

        if (_effect == Effect::HealCharge) {
            uint32_t elapsed = now - _seqStart;
            if (elapsed >= 1200) {
                _effect = Effect::None;
                _drawHpBar();
                return;
            }
            if (elapsed < 750) {
                // Phase 1: green bar fills from LED 0 (back) → LED 5 (front).
                float ledsF = (float)elapsed * (float)LED_STRIP_COUNT / 750.0f;
                int fullLeds = (int)ledsF;
                uint8_t partialBright = (uint8_t)((ledsF - (float)fullLeds) * 220.0f);
                _strip.clear();
                for (int i = 0; i < fullLeds && i < LED_STRIP_COUNT; i++)
                    _strip.setPixelColor(i, _strip.Color(0, 220, 0));
                if (fullLeds < LED_STRIP_COUNT && partialBright > 0)
                    _strip.setPixelColor(fullLeds, _strip.Color(0, partialBright, 0));
                _strip.show();
            } else {
                // Phase 2: rapid white flashes (90 ms on / 90 ms off).
                bool flashOn = ((elapsed - 750u) % 90u) < 45u;
                if (flashOn) _fillStrip(255, 255, 255);
                else { _strip.clear(); _strip.show(); }
            }
            return;
        }

        if (_effect == Effect::DeathExplosion) {
            uint32_t elapsed = now - _seqStart;

            if (elapsed < 300u) {
                // Phase 1: burst outward in 3 waves (one pair per 100 ms)
                int wave = (int)(elapsed / 100u);
                _strip.clear();
                if (wave >= 0) { // center pair: red
                    _strip.setPixelColor(2, _strip.Color(255,  20, 0));
                    _strip.setPixelColor(3, _strip.Color(255,  20, 0));
                }
                if (wave >= 1) { // middle pair: orange
                    _strip.setPixelColor(1, _strip.Color(255,  90, 0));
                    _strip.setPixelColor(4, _strip.Color(255,  90, 0));
                }
                if (wave >= 2) { // outer pair: yellow
                    _strip.setPixelColor(0, _strip.Color(255, 180, 0));
                    _strip.setPixelColor(5, _strip.Color(255, 180, 0));
                }
                _strip.show();

            } else if (elapsed < 5000u) {
                // Phase 2: fire flicker fading over 4700 ms
                if ((int32_t)(now - _deathFlickerNext) >= 0) {
                    _deathFlickerNext = now + 20;
                    uint32_t p2  = elapsed - 300u;
                    float    age = (float)p2 / 4700.0f;
                    float    amp = 1.0f - age;
                    for (int i = 0; i < LED_STRIP_COUNT; i++) {
                        uint32_t period = 70u + (uint32_t)((unsigned)i * 23u);
                        uint32_t phase  = (p2 + (uint32_t)((unsigned)i * 113u)) % period;
                        float flick  = (phase < period * 2u / 3u) ? 1.0f : 0.2f;
                        float bright = amp * flick;
                        // Center LEDs: deeper red; outer LEDs: more orange
                        float gFrac = (i == 2 || i == 3) ? 0.12f
                                    : (i == 1 || i == 4) ? 0.25f : 0.45f;
                        uint8_t r = (uint8_t)(bright * 255.0f);
                        uint8_t g = (uint8_t)(bright * gFrac * 255.0f);
                        _strip.setPixelColor(i, _strip.Color(r, g, 0));
                    }
                    _strip.show();
                }

            } else {
                // Transition to dead blink
                _effect          = Effect::DeadBlink;
                _deadBlinkOn     = false;
                _deadBlinkToggle = now + 500;
                _drawDeadBlink();
            }
            return;
        }

        if (_effect == Effect::DeadBlink) {
            if ((int32_t)(now - _deadBlinkToggle) >= 0) {
                _deadBlinkOn     = !_deadBlinkOn;
                _deadBlinkToggle = now + 500;
                _drawDeadBlink();
            }
            return;
        }

        // Fire / Hit timed effects: wait for expiry then restore HP bar.
        if ((int32_t)(now - _effectEnd) >= 0) {
            _effect = Effect::None;
            _drawHpBar();
        }
    }

    // Drive the 0.5 s warning pulse when HP is in the last LED's territory.
    void _updatePulse(uint32_t now)
    {
        if (_effect != Effect::None || _hp <= 0) return; // suppress during any effect
        float ledsF = (float)_hp * LED_STRIP_COUNT / _maxHp;
        if (ledsF > 1.0f) return; // only pulse at last LED

        if ((int32_t)(now - _nextPulseToggle) >= 0) {
            _pulseOn         = !_pulseOn;
            _nextPulseToggle = now + 500;
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

    // Draw the HP bar with proportional brightness.
    // LED 5 (front) is the last to dim; LED 0 (back) dims first.
    // Full LEDs are bright blue; the transition LED uses partial brightness.
    // When in the last LED's territory, _pulseOn gates the whole display.
    void _drawHpBar()
    {
        _strip.clear();
        if (_hp > 0 && _maxHp > 0) {
            float ledsF = (float)_hp * LED_STRIP_COUNT / _maxHp;
            if (ledsF > (float)LED_STRIP_COUNT) ledsF = (float)LED_STRIP_COUNT;

            int fullLeds = (int)ledsF;
            uint8_t partialBright = (uint8_t)((ledsF - fullLeds) * (float)DIM_BRIGHTNESS);

            bool warning = (ledsF <= 1.0f);
            if (!warning || _pulseOn) {
                // Full LEDs at the front (high indices: 5, 4, ... down)
                int firstFull = LED_STRIP_COUNT - fullLeds;
                for (int i = firstFull; i < LED_STRIP_COUNT; i++)
                    _strip.setPixelColor(i, _strip.Color(0, 0, DIM_BRIGHTNESS));
                // Partial LED just below the full ones
                int partialIdx = firstFull - 1;
                if (partialBright > 0 && partialIdx >= 0)
                    _strip.setPixelColor(partialIdx, _strip.Color(0, 0, partialBright));
            }
        }
        _strip.show();
    }

    // Dim red alternating pattern for dead-walk state.
    // Even call: LEDs 0,2,4 lit. Odd call: LEDs 1,3,5 lit.
    void _drawDeadBlink()
    {
        _strip.clear();
        uint8_t r = DIM_BRIGHTNESS;
        if (_deadBlinkOn) {
            _strip.setPixelColor(0, _strip.Color(r, 0, 0));
            _strip.setPixelColor(2, _strip.Color(r, 0, 0));
            _strip.setPixelColor(4, _strip.Color(r, 0, 0));
        } else {
            _strip.setPixelColor(1, _strip.Color(r, 0, 0));
            _strip.setPixelColor(3, _strip.Color(r, 0, 0));
            _strip.setPixelColor(5, _strip.Color(r, 0, 0));
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
    uint32_t          _seqStart          = 0;
    int               _hp                = 0;   // dark until set_hp received from server
    int               _maxHp             = 100;
    bool              _pulseOn           = true;  // warning-pulse display state
    uint32_t          _nextPulseToggle   = 0;

    // Death-explosion / dead-blink state
    bool              _deadBlinkOn       = false;
    uint32_t          _deadBlinkToggle   = 0;
    uint32_t          _deathFlickerNext  = 0;

    StatusPattern     _statusPattern     = StatusPattern::SearchingFast;
    uint32_t          _statusNextToggle  = 0;
    bool              _statusLedOn       = false;
};
