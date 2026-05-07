// CameraController.h — OV2640 camera initialisation for the v0.2 turret board.
//
// GPIO assignments are corrected from the early prototype:
//   PCLK  = GPIO08  (was 13 — wrong in old code)
//   XCLK  = GPIO16  (was 15 — wrong in old code)
//   Data lines reordered to match v0.2 schematic (Y2-Y9 routing changed).
//
// LEDC note: esp_camera_init uses LEDC_CHANNEL_0 / LEDC_TIMER_0 for XCLK.
// IrController and the buzzer use channels 4-6 on separate timers, so there
// is no conflict.  Do not change ledc_channel / ledc_timer here.
//
// The camera does NOT stream over WebSocket.  Frames are served by MjpegServer
// running its own HTTP handler, so CameraController only manages init/deinit.
// Orientation flags (vflip, hmirror) may need adjusting depending on how the
// camera module is physically mounted in the turret.

#pragma once
#include <Arduino.h>
#include "esp_camera.h"

class CameraController
{
public:
    CameraController()
      : _started(false)
      , _frameSize(FRAMESIZE_HVGA) // 480×320
      , _jpegQuality(10)          // 0=best quality, 63=smallest file; 10 is a good balance
      , _vflip(1)
      , _hmirror(1)
    {
        memset(&_cfg, 0, sizeof(_cfg));
    }

    // Store pin config.  Does not touch hardware; call before start().
    void begin()
    {
        // SCCB (sensor register bus)
        _cfg.pin_sccb_sda  =  4;   // GPIO04 SIOD
        _cfg.pin_sccb_scl  =  5;   // GPIO05 SIOC

        // Frame timing
        _cfg.pin_vsync     =  6;   // GPIO06 VSYNC
        _cfg.pin_href      =  7;   // GPIO07 HREF
        _cfg.pin_pclk      =  8;   // GPIO08 PCLK  ← corrected (was 13)
        _cfg.pin_xclk      = 16;   // GPIO16 XCLK  ← corrected (was 15)

        // Pixel data — D0(Y2) through D7(Y9), per v0.2 turret board schematic
        _cfg.pin_d0        = 10;   // Y2  GPIO10
        _cfg.pin_d1        = 12;   // Y3  GPIO12
        _cfg.pin_d2        = 13;   // Y4  GPIO13
        _cfg.pin_d3        = 11;   // Y5  GPIO11
        _cfg.pin_d4        =  9;   // Y6  GPIO09
        _cfg.pin_d5        = 18;   // Y7  GPIO18
        _cfg.pin_d6        = 17;   // Y8  GPIO17
        _cfg.pin_d7        = 15;   // Y9  GPIO15

        // Power pins — not broken out on this board design
        _cfg.pin_pwdn      = -1;   // PWDN tied to GND on board
        _cfg.pin_reset     = -1;   // RESETB pulled high on board

        // LEDC for XCLK generation — do not conflict with IR (channels 4-6)
        _cfg.ledc_channel  = LEDC_CHANNEL_0;
        _cfg.ledc_timer    = LEDC_TIMER_0;
        _cfg.xclk_freq_hz  = 20000000; // 20 MHz

        _cfg.pixel_format  = PIXFORMAT_JPEG;
        _cfg.frame_size    = _frameSize;
        _cfg.jpeg_quality  = _jpegQuality;
        _cfg.fb_count      = 2;                 // double-buffer in PSRAM
        _cfg.fb_location   = CAMERA_FB_IN_PSRAM;
        _cfg.grab_mode     = CAMERA_GRAB_LATEST; // always return the newest frame
    }

    // Initialise and power up the camera driver (idempotent).
    bool start()
    {
        if (_started) return true;

        Serial.println("[CAM] init...");
        esp_err_t err = esp_camera_init(&_cfg);
        if (err != ESP_OK) {
            Serial.printf("[CAM] init failed: 0x%x\n", (int)err);
            return false;
        }

        if (sensor_t* s = esp_camera_sensor_get()) {
            s->set_framesize(s, _frameSize);
            s->set_quality(s, _jpegQuality);
            s->set_brightness(s, 0);
            s->set_saturation(s, 0);
            s->set_vflip(s, _vflip);
            s->set_hmirror(s, _hmirror);
        }

        _started = true;
        Serial.println("[CAM] ready");
        return true;
    }

    // Shut down the camera driver (idempotent).  Called before OTA and on stream_off.
    void stop()
    {
        if (!_started) return;
        esp_camera_deinit();
        _started = false;
        Serial.println("[CAM] stopped");
    }

    bool isStarted() const { return _started; }

    // Apply flip/mirror at runtime. Safe to call before or after start().
    void applyFlip(bool vflip, bool hmirror)
    {
        _vflip   = vflip   ? 1 : 0;
        _hmirror = hmirror ? 1 : 0;
        if (_started) {
            if (sensor_t* s = esp_camera_sensor_get()) {
                s->set_vflip(s,   _vflip);
                s->set_hmirror(s, _hmirror);
            }
        }
    }

    int getVFlip()   const { return _vflip; }
    int getHMirror() const { return _hmirror; }

private:
    camera_config_t _cfg;
    bool            _started;
    framesize_t     _frameSize;
    int             _jpegQuality;
    int             _vflip;
    int             _hmirror;
};
