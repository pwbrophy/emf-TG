// LedController.h — drives two WS2812 outputs from one BeaconState:
//   - external 9-LED strip on GPIO4 (the physical capture-point fixture)
//   - the dev board's onboard single-pixel WS2812 on GPIO48
//
// Both outputs always render the same logical state, so the onboard pixel
// lets you validate all beacon lighting behaviour before soldering the strip.
//
// States:
//   BootHw/BootWifi/BootServer — centre strip pixel + onboard pixel, dim
//                                 red/green/blue (~10% brightness)
//   Idle                       — strip: bouncing white dot; onboard: flashing white
//   Unlit                      — everything off (match started, point uncaptured)
//   Captured                   — solid fill in the capturing alliance's colour

#pragma once
#include <Arduino.h>
#include <Adafruit_NeoPixel.h>

static constexpr int STRIP_PIN     = 4;   // external WS2812 strip data line
static constexpr int STRIP_COUNT   = 9;
static constexpr int STRIP_CENTRE  = 4;   // middle LED, index 0..8
static constexpr int ONBOARD_PIN   = 48;  // dev board's built-in WS2812 pixel
static constexpr int ONBOARD_COUNT = 1;
static constexpr uint8_t DIM_BRIGHTNESS = 26; // ~10% of 255 — matches robot firmware

enum class BeaconState : uint8_t { BootHw, BootWifi, BootServer, Idle, Unlit, Captured };

class LedController
{
public:
    LedController()
      : _strip(STRIP_COUNT, STRIP_PIN, NEO_GRB + NEO_KHZ800)
      , _onboard(ONBOARD_COUNT, ONBOARD_PIN, NEO_GRB + NEO_KHZ800)
    {}

    void begin()
    {
        _strip.begin();   _strip.clear();   _strip.show();
        _onboard.begin(); _onboard.clear(); _onboard.show();
        setState(BeaconState::BootHw);
    }

    // Must be called every loop tick — only the Idle animation needs it,
    // other states are static and only redraw on setState()/setCaptured().
    void update(uint32_t now)
    {
        if (_state == BeaconState::Idle) _updateIdle(now);
    }

    void setState(BeaconState s)
    {
        _state      = s;
        _bouncePos  = 0;
        _bounceDir  = 1;
        _bounceNext = 0;
        _flashOn    = true;
        _flashNext  = 0;
        _draw();
    }

    // Sets the capture colour and switches to the Captured state in one call.
    void setCaptured(uint8_t r, uint8_t g, uint8_t b)
    {
        _capR = r; _capG = g; _capB = b;
        setState(BeaconState::Captured);
    }

    // OTA progress bar — bypasses the state machine (update() may not run
    // during OTA). Strip fills 0..8 in dim purple proportional to pct;
    // the onboard pixel brightens proportionally in purple as a single-LED
    // analogue of the same bar.
    void showOtaProgress(uint8_t pct)
    {
        _strip.clear();
        float   ledsF    = (float)pct * STRIP_COUNT / 100.0f;
        int     fullLeds = (int)ledsF;
        for (int i = 0; i < fullLeds; i++)
            _strip.setPixelColor(i, _strip.Color(DIM_BRIGHTNESS, 0, DIM_BRIGHTNESS));
        uint8_t partial = (uint8_t)((ledsF - (float)fullLeds) * DIM_BRIGHTNESS);
        if (partial > 0 && fullLeds < STRIP_COUNT)
            _strip.setPixelColor(fullLeds, _strip.Color(partial, 0, partial));
        _strip.show();

        uint8_t b = (uint8_t)((uint32_t)pct * 255u / 100u);
        _onboard.setPixelColor(0, _onboard.Color(b, 0, b));
        _onboard.show();
    }

private:
    void _draw()
    {
        switch (_state)
        {
        case BeaconState::BootHw:     _drawBootColor(DIM_BRIGHTNESS, 0, 0); break;
        case BeaconState::BootWifi:   _drawBootColor(0, DIM_BRIGHTNESS, 0); break;
        case BeaconState::BootServer: _drawBootColor(0, 0, DIM_BRIGHTNESS); break;
        case BeaconState::Unlit:      _fillBoth(0, 0, 0); break;
        case BeaconState::Captured:   _fillBoth(_capR, _capG, _capB); break;
        case BeaconState::Idle:
            // Left blank; update() draws the first bounce/flash frame within
            // one loop tick (sub-millisecond), same "force redraw" approach
            // the robot firmware uses for its boot-phase transitions.
            _strip.clear();   _strip.show();
            _onboard.clear(); _onboard.show();
            break;
        }
    }

    void _drawBootColor(uint8_t r, uint8_t g, uint8_t b)
    {
        _strip.clear();
        _strip.setPixelColor(STRIP_CENTRE, _strip.Color(r, g, b));
        _strip.show();
        _onboard.setPixelColor(0, _onboard.Color(r, g, b));
        _onboard.show();
    }

    void _fillBoth(uint8_t r, uint8_t g, uint8_t b)
    {
        for (int i = 0; i < STRIP_COUNT; i++)
            _strip.setPixelColor(i, _strip.Color(r, g, b));
        _strip.show();
        _onboard.setPixelColor(0, _onboard.Color(r, g, b));
        _onboard.show();
    }

    // Strip: bouncing white dot, symmetric dwell time (fastest through the
    // centre), same shape as the robot's idle bounce, resized 6->9 LEDs.
    // Onboard: flashing white at 1 Hz (a single pixel can't bounce).
    void _updateIdle(uint32_t now)
    {
        static const uint32_t BOUNCE_MS[STRIP_COUNT] = {200,150,110,80,55,80,110,150,200};

        if ((int32_t)(now - _bounceNext) >= 0)
        {
            _bounceNext = now + BOUNCE_MS[_bouncePos];
            _strip.clear();
            _strip.setPixelColor(_bouncePos, _strip.Color(13, 13, 13)); // ~5% white
            _strip.show();
            _bouncePos += _bounceDir;
            if (_bouncePos >= STRIP_COUNT) { _bouncePos = STRIP_COUNT - 2; _bounceDir = -1; }
            if (_bouncePos < 0)             { _bouncePos = 1;              _bounceDir =  1; }
        }

        if ((int32_t)(now - _flashNext) >= 0)
        {
            _flashOn   = !_flashOn;
            _flashNext = now + 500;
            uint8_t b  = _flashOn ? 30 : 0; // dim — full white at close range was blinding
            _onboard.setPixelColor(0, _onboard.Color(b, b, b));
            _onboard.show();
        }
    }

    Adafruit_NeoPixel _strip;
    Adafruit_NeoPixel _onboard;
    BeaconState       _state = BeaconState::BootHw;

    uint8_t _capR = 0, _capG = 0, _capB = 0;

    int      _bouncePos  = 0;
    int      _bounceDir  = 1;
    uint32_t _bounceNext = 0;

    bool     _flashOn   = true;
    uint32_t _flashNext = 0;
};
