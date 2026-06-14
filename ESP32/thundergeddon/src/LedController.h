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

// Boot-progress phase shown on the WS2812B strip.
// Phases are ordered so uint8_t comparison works (>= tricks avoided; see _drawBootStatus).
enum class BootPhase : uint8_t {
    HwInit,           // 0,1 = 5% dim red;            2–5 = off
    HwDone,           // 0,1 = solid red;              2–5 = off
    WifiConnecting,   // 0,1 = solid red;   2,3 = flashing green;     4,5 = off
    WifiDone,         // 0,1 = solid red;   2,3 = solid green;         4,5 = off
    ServerConnecting, // 0,1 = solid red;   2,3 = solid green;  4,5 = flashing blue
    ServerDone,       // all clear; low-power white dot bouncing 0↔5
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
        _drawBootStatus();    // show initial dim-red on LEDs 0,1
    }

    // Must be called every loop tick to advance timed effects and blink patterns.
    void update(uint32_t now)
    {
        _updateEffect(now);
        _updatePulse(now);
        _updateStatus(now);
        _updateBootAnim(now);
    }

    // Advance the boot-progress overlay.  Call at key points in setup():
    //   HwDone           — after all hardware is initialised
    //   WifiConnecting   — just before connectWifi()
    //   WifiDone         — just after connectWifi() returns
    //   ServerConnecting — when entering the discovery/connect loop
    //   ServerDone       — when the WebSocket connection opens
    void setBootPhase(BootPhase p)
    {
        _bootPhase    = p;
        _bootFlashOn  = true;
        _bootNextFlash = 0; // force immediate draw on next update
        _bouncePos    = 0;
        _bounceDir    = 1;
        _bounceNext   = 0;
        if (_effect == Effect::None) {
            if (p == BootPhase::ServerDone) {
                _strip.clear();
                _strip.show();
            } else {
                _drawBootStatus();
            }
        }
    }

    // ---- HP bar ----

    // Update stored HP values and redraw the bar.
    // If hp > 0 arrives while in a death state, cancel it — the server says the
    // robot is alive again (e.g. game reset), so restore the HP bar immediately.
    void setHp(int hp, int maxHp)
    {
        _hp    = (hp    < 0) ? 0    : hp;
        _maxHp = (maxHp < 1) ? 1    : maxHp;
        _pulseOn         = true;
        _nextPulseToggle = millis() + 500;
        if (_hp > 0 && (_effect == Effect::DeadBlink || _effect == Effect::DeathExplosion))
            _effect = Effect::None;
        if (_effect == Effect::None && !_playerColorActive) _drawHpBar();
    }

    // ---- Player identification colour ----

    // Pulse all 6 LEDs in the given colour while in the lobby / countdown.
    // Call clearPlayerColor() when the game starts (game_start_fanfare command).
    void setPlayerColor(uint8_t r, uint8_t g, uint8_t b)
    {
        _pcR = r; _pcG = g; _pcB = b;
        _playerColorActive = true;
        _countdownLeds     = -1;
        if (_effect == Effect::None) _drawPlayerBar();
    }

    void clearPlayerColor()
    {
        _playerColorActive = false;
        _countdownLeds     = -1;
        if (_effect == Effect::None) _drawHpBar();
    }

    // Countdown bar: show count/total LEDs solid in player colour.
    // Call each countdown_tick. Ignored if no player colour is active.
    void setCountdownTick(int count, int total)
    {
        if (!_playerColorActive) return;
        if (total <= 0) total = 1;
        _countdownLeds = (int)roundf((float)count * (float)LED_STRIP_COUNT / (float)total);
        if (_countdownLeds > LED_STRIP_COUNT) _countdownLeds = LED_STRIP_COUNT;
        if (_countdownLeds < 0)               _countdownLeds = 0;
        if (_effect == Effect::None) _drawPlayerBar();
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

    // Capture celebration: gold LEDs fill from the outside in (0&5 → 1&4 → 2&3),
    // hold for 200 ms, then restore HP bar. Total 650 ms.
    void captureSequence()
    {
        if (_effect == Effect::Hit) return; // hit takes priority
        _effect   = Effect::CaptureSeq;
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

    // OTA progress bar — bypasses the effect system (update() is not running during OTA).
    // Fills LEDs 0→5 in dim purple proportional to pct (0–100).
    void showOtaProgress(uint8_t pct)
    {
        _strip.clear();
        float   ledsF    = (float)pct * LED_STRIP_COUNT / 100.0f;
        int     fullLeds = (int)ledsF;
        for (int i = 0; i < fullLeds; i++)
            _strip.setPixelColor(i, _strip.Color(DIM_BRIGHTNESS, 0, DIM_BRIGHTNESS));
        uint8_t partial = (uint8_t)((ledsF - (float)fullLeds) * DIM_BRIGHTNESS);
        if (partial > 0 && fullLeds < LED_STRIP_COUNT)
            _strip.setPixelColor(fullLeds, _strip.Color(partial, 0, partial));
        _strip.show();
    }

private:
    enum class Effect : uint8_t { None, Fire, Hit, FireSeq, HealCharge, CaptureSeq, DeathExplosion, DeadBlink };

    // Check whether the active timed effect has expired; drive animations.
    void _updateEffect(uint32_t now)
    {
        if (_effect == Effect::None) return;

        if (_effect == Effect::FireSeq) {
            // Forward launch sweep: all white at t=0, extinguish front→back over 150 ms.
            // Matches the buzzer static BANG duration for immediate tactile sync.
            uint32_t elapsed = now - _seqStart;
            if (elapsed >= 200) {
                _effect = Effect::None;
                _restoreBackground();
                return;
            }
            int ledsOff = (int)(elapsed * LED_STRIP_COUNT / 200);
            _strip.clear();
            for (int i = 0; i < LED_STRIP_COUNT - ledsOff; i++)
                _strip.setPixelColor(i, _strip.Color(255, 255, 255));
            _strip.show();
            return;
        }

        if (_effect == Effect::HealCharge) {
            uint32_t elapsed = now - _seqStart;
            if (elapsed >= 1200) {
                _effect = Effect::None;
                _restoreBackground();
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

        if (_effect == Effect::CaptureSeq) {
            // Phase 1 (0–450 ms): gold LEDs close in from both ends, one pair per 150 ms.
            // Phase 2 (450–650 ms): hold all-gold.
            uint32_t elapsed = now - _seqStart;
            if (elapsed >= 650) {
                _effect = Effect::None;
                _restoreBackground();
                return;
            }
            if (elapsed < 450) {
                int step = (int)(elapsed / 150u); // 0, 1, or 2
                _strip.clear();
                // step 0: pair 0 & 5; step 1: also 1 & 4; step 2: also 2 & 3
                for (int s = 0; s <= step; s++) {
                    _strip.setPixelColor(s,                   _strip.Color(255, 165, 0));
                    _strip.setPixelColor(LED_STRIP_COUNT-1-s, _strip.Color(255, 165, 0));
                }
                _strip.show();
            } else {
                // Phase 2: all six gold
                _fillStrip(255, 165, 0);
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
            _restoreBackground();
        }
    }

    // Drive the 0.5 s warning pulse when HP is in the last LED's territory.
    void _updatePulse(uint32_t now)
    {
        if (_effect != Effect::None || _hp <= 0 || _playerColorActive) return;
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

    // Draw all LEDs in player's solid colour (or countdown bar).
    void _drawPlayerBar()
    {
        _strip.clear();
        int leds = (_countdownLeds < 0) ? LED_STRIP_COUNT : _countdownLeds;
        for (int i = 0; i < leds; i++)
            _strip.setPixelColor(i, _strip.Color(
                (uint8_t)((uint32_t)_pcR * 200 / 255),
                (uint8_t)((uint32_t)_pcG * 200 / 255),
                (uint8_t)((uint32_t)_pcB * 200 / 255)));
        _strip.show();
    }

    // Called when a timed effect expires to restore whatever was showing before it.
    void _restoreBackground()
    {
        if (_playerColorActive)
            _drawPlayerBar();
        else
            _drawHpBar();
    }

    // ---- Boot-progress overlay ----

    // Render a static snapshot of the current boot phase.
    // Called immediately on phase change and on each flash toggle.
    void _drawBootStatus()
    {
        static constexpr uint8_t DIM5 = 13;  // ~5% of 255

        _strip.clear();

        // LEDs 0,1 — power / hardware status
        _strip.setPixelColor(0, _strip.Color(DIM5, 0, 0));
        _strip.setPixelColor(1, _strip.Color(DIM5, 0, 0));

        // LEDs 2,3 — Wi-Fi status (only from WifiConnecting onwards)
        if (_bootPhase == BootPhase::WifiConnecting ||
            _bootPhase == BootPhase::WifiDone       ||
            _bootPhase == BootPhase::ServerConnecting) {
            bool gOn = (_bootPhase != BootPhase::WifiConnecting) || _bootFlashOn;
            if (gOn) {
                _strip.setPixelColor(2, _strip.Color(0, DIM5, 0));
                _strip.setPixelColor(3, _strip.Color(0, DIM5, 0));
            }
        }

        // LEDs 4,5 — server / WebSocket status (only from ServerConnecting onwards)
        if (_bootPhase == BootPhase::ServerConnecting) {
            if (_bootFlashOn) {
                _strip.setPixelColor(4, _strip.Color(0, 0, DIM5));
                _strip.setPixelColor(5, _strip.Color(0, 0, DIM5));
            }
        }

        _strip.show();
    }

    // Called every update() tick to handle flash timing and the post-boot bounce.
    void _updateBootAnim(uint32_t now)
    {
        if (_effect != Effect::None) return;

        if (_bootPhase == BootPhase::WifiConnecting ||
            _bootPhase == BootPhase::ServerConnecting) {
            if ((int32_t)(now - _bootNextFlash) >= 0) {
                _bootFlashOn   = !_bootFlashOn;
                _bootNextFlash = now + 300;
                _drawBootStatus();
            }
            return;
        }

        // Player colour is drawn statically by setPlayerColor/setCountdownTick — don't overwrite with bounce.
        if (_playerColorActive) return;

        if (_bootPhase == BootPhase::ServerDone && _hp == 0) {
            if ((int32_t)(now - _bounceNext) >= 0) {
                static const uint32_t BD[LED_STRIP_COUNT] = {200, 110, 55, 55, 110, 200};
                _bounceNext = now + BD[_bouncePos];
                _strip.clear();
                _strip.setPixelColor(_bouncePos, _strip.Color(13, 13, 13)); // ~5% white
                _strip.show();
                _bouncePos += _bounceDir;
                if (_bouncePos >= LED_STRIP_COUNT) { _bouncePos = LED_STRIP_COUNT - 2; _bounceDir = -1; }
                if (_bouncePos < 0)                { _bouncePos = 1;                   _bounceDir =  1; }
            }
        }
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

    // Boot-progress overlay
    BootPhase         _bootPhase         = BootPhase::HwInit;
    bool              _bootFlashOn       = true;
    uint32_t          _bootNextFlash     = 0;

    // ServerDone idle bounce
    int               _bouncePos         = 0;
    int               _bounceDir         = 1;
    uint32_t          _bounceNext        = 0;

    // Player identification colour (lobby phase)
    bool              _playerColorActive = false;
    uint8_t           _pcR               = 0;
    uint8_t           _pcG               = 0;
    uint8_t           _pcB               = 0;
    int               _countdownLeds     = -1; // -1 = full bar; 0..6 = countdown bar
};
