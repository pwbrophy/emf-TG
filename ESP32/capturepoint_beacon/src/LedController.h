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
//   CaptureRipple              — ~1s animated transition into Captured: white
//                                 ripples close in from both ends to the centre,
//                                 the target colour brightening in behind each pass
//   VpRipple                   — ~0.5s single white ripple, outside->centre, then
//                                 restores whatever was showing beforehand (used
//                                 to mirror the spectator display's score-tick flash)

#pragma once
#include <Arduino.h>
#include <Adafruit_NeoPixel.h>

static constexpr int STRIP_PIN     = 4;   // external WS2812 strip data line
static constexpr int STRIP_COUNT   = 9;
static constexpr int STRIP_CENTRE  = 4;   // middle LED, index 0..8
static constexpr int ONBOARD_PIN   = 48;  // dev board's built-in WS2812 pixel
static constexpr int ONBOARD_COUNT = 1;
static constexpr uint8_t DIM_BRIGHTNESS = 26; // ~10% of 255 — matches robot firmware

static constexpr uint32_t CAPTURE_RIPPLE_MS    = 1000;
static constexpr int      CAPTURE_RIPPLE_WAVES = 3;
static constexpr uint32_t VP_RIPPLE_MS         = 500;

enum class BeaconState : uint8_t
{
    BootHw, BootWifi, BootServer, Idle, Unlit, Captured, CaptureRipple, VpRipple
};

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

    // Must be called every loop tick — Idle and the two ripple animations
    // need it; other states are static and only redraw on setState().
    void update(uint32_t now)
    {
        switch (_state)
        {
        case BeaconState::Idle:          _updateIdle(now);          break;
        case BeaconState::CaptureRipple: _updateCaptureRipple(now); break;
        case BeaconState::VpRipple:      _updateVpRipple(now);      break;
        default: break;
        }
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

    // Instantly sets the capture colour and switches to the Captured state,
    // no animation. Used for hello-resync (a beacon reconnecting mid-match
    // shouldn't replay the capture animation) and as the CaptureRipple's
    // settle-to-final-colour step.
    void setCaptured(uint8_t r, uint8_t g, uint8_t b)
    {
        _capR = r; _capG = g; _capB = b;
        setState(BeaconState::Captured);
    }

    // Starts the ~1s capture animation: white ripples close in from both
    // ends to the centre (several passes), the target colour brightening in
    // behind each pass until the strip settles solid in that colour.
    void startCaptureRipple(uint8_t r, uint8_t g, uint8_t b)
    {
        _capR = r; _capG = g; _capB = b;
        _animStart = millis();
        _state     = BeaconState::CaptureRipple;
    }

    // Starts a brief single white ripple (outside->centre) on top of
    // whatever is currently showing, then restores it. Used to mirror the
    // spectator display's per-tick score flash on the physical beacon.
    void startVpRipple()
    {
        if (_state == BeaconState::CaptureRipple) return; // don't interrupt the capture animation
        _priorState = _state;
        _animStart  = millis();
        _state      = BeaconState::VpRipple;
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
    static inline float _fabs(float x) { return x < 0.0f ? -x : x; }

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
        case BeaconState::CaptureRipple:
        case BeaconState::VpRipple:
            // Animated states — left blank here; update() draws the first
            // frame within one loop tick (sub-millisecond), same "force
            // redraw" approach the robot firmware uses for boot-phase
            // transitions.
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

    // Several (CAPTURE_RIPPLE_WAVES) white wavefronts sweep from both ends
    // (distance 0) to the centre (distance 4) in turn. Behind each pass the
    // target colour brightens up a further 1/N step, so by the final wave
    // the strip is fully in the capture colour with no more white.
    void _updateCaptureRipple(uint32_t now)
    {
        uint32_t elapsed = now - _animStart;
        if (elapsed >= CAPTURE_RIPPLE_MS)
        {
            setCaptured(_capR, _capG, _capB);
            return;
        }

        const float waveDur = (float)CAPTURE_RIPPLE_MS / (float)CAPTURE_RIPPLE_WAVES;
        int   waveIdx = (int)((float)elapsed / waveDur);
        if (waveIdx >= CAPTURE_RIPPLE_WAVES) waveIdx = CAPTURE_RIPPLE_WAVES - 1;
        float waveT   = ((float)elapsed - (float)waveIdx * waveDur) / waveDur; // 0..1 within this wave
        float front   = waveT * 4.0f;                                          // 0 (ends) .. 4 (centre)
        float mix     = (float)(waveIdx + 1) / (float)CAPTURE_RIPPLE_WAVES;    // 1/3, 2/3, 1

        for (int i = 0; i < STRIP_COUNT; i++)
        {
            float d = (float)((i < STRIP_COUNT - 1 - i) ? i : (STRIP_COUNT - 1 - i)); // dist from nearest end
            uint8_t r = (uint8_t)((float)_capR * mix);
            uint8_t g = (uint8_t)((float)_capG * mix);
            uint8_t b = (uint8_t)((float)_capB * mix);
            if (_fabs(d - front) < 0.8f) { r = g = b = 255; } // white wavefront overrides
            _strip.setPixelColor(i, _strip.Color(r, g, b));
        }
        _strip.show();

        // Onboard: flash near the start of each wave (the "ripple launching"
        // moment), otherwise show the same brightening blend.
        if (waveT < 0.3f)
        {
            _onboard.setPixelColor(0, _onboard.Color(150, 150, 150)); // dimmer than the strip's flash — close-range pixel
        }
        else
        {
            _onboard.setPixelColor(0, _onboard.Color(
                (uint8_t)((float)_capR * mix), (uint8_t)((float)_capG * mix), (uint8_t)((float)_capB * mix)));
        }
        _onboard.show();
    }

    // Single white wavefront, outside->centre, over the current background
    // colour (the stored capture colour if we were Captured; black/off
    // otherwise), then restores whatever state was active before.
    void _updateVpRipple(uint32_t now)
    {
        uint32_t elapsed = now - _animStart;
        if (elapsed >= VP_RIPPLE_MS)
        {
            setState(_priorState);
            return;
        }

        float t     = (float)elapsed / (float)VP_RIPPLE_MS;
        float front = t * 4.0f;

        uint8_t baseR = 0, baseG = 0, baseB = 0;
        if (_priorState == BeaconState::Captured) { baseR = _capR; baseG = _capG; baseB = _capB; }

        for (int i = 0; i < STRIP_COUNT; i++)
        {
            float d = (float)((i < STRIP_COUNT - 1 - i) ? i : (STRIP_COUNT - 1 - i));
            uint8_t r = baseR, g = baseG, b = baseB;
            if (_fabs(d - front) < 0.8f) { r = g = b = 255; }
            _strip.setPixelColor(i, _strip.Color(r, g, b));
        }
        _strip.show();

        if (front < 3.0f) _onboard.setPixelColor(0, _onboard.Color(150, 150, 150));
        else               _onboard.setPixelColor(0, _onboard.Color(baseR, baseG, baseB));
        _onboard.show();
    }

    Adafruit_NeoPixel _strip;
    Adafruit_NeoPixel _onboard;
    BeaconState       _state      = BeaconState::BootHw;
    BeaconState       _priorState = BeaconState::Idle; // what VpRipple restores when done

    uint8_t _capR = 0, _capG = 0, _capB = 0;

    uint32_t _animStart = 0; // shared start time for CaptureRipple / VpRipple

    int      _bouncePos  = 0;
    int      _bounceDir  = 1;
    uint32_t _bounceNext = 0;

    bool     _flashOn   = true;
    uint32_t _flashNext = 0;
};
