// MjpegServer.h — HTTP MJPEG stream server on port 81.
//
// Phones connect directly to  http://<robot-ip>:81/stream  via an <img> tag
// in the player SPA.  This removes Unity from the video path entirely.
//
// Uses esp_http_server (ESP-IDF built-in) rather than ESPAsyncWebServer because
// the async TCP stack has known instability on ESP32-S3 under simultaneous
// WebSocket load.  esp_http_server runs the streaming handler in its own
// FreeRTOS task; it blocks inside the handler while the client is connected
// and does not touch the Arduino loop at all.
//
// Thread safety notes:
//   _enabled is volatile bool — single-byte read/write is atomic on ARM.
//   esp_camera_fb_get/return are thread-safe in the ESP-IDF camera driver.
//   No other shared state is accessed from the handler task.
//
// Usage:
//   mjpeg.begin()           — call once after WiFi connects (binds port 81)
//   mjpeg.setEnabled(true)  — call on stream_on after cam.start()
//   mjpeg.setEnabled(false) — call on stream_off
//   mjpeg.stop()            — call before OTA update

#pragma once
#include <Arduino.h>
#include "esp_http_server.h"
#include "esp_camera.h"

class MjpegServer
{
public:
    // Register the /stream endpoint and start the HTTP server on port 81.
    bool begin()
    {
        httpd_config_t cfg  = HTTPD_DEFAULT_CONFIG();
        cfg.server_port     = 81;
        cfg.ctrl_port       = 32769;  // avoid clash with default 32768
        cfg.stack_size      = 8192;
        cfg.max_open_sockets = 2;     // one viewer at a time is enough; allow a spare slot

        if (httpd_start(&_server, &cfg) != ESP_OK) {
            Serial.println("[MJPEG] httpd_start failed");
            _server = nullptr;
            return false;
        }

        httpd_uri_t uri = {};
        uri.uri      = "/stream";
        uri.method   = HTTP_GET;
        uri.handler  = _streamHandler;
        uri.user_ctx = this;
        httpd_register_uri_handler(_server, &uri);

        Serial.println("[MJPEG] server ready on :81/stream");
        return true;
    }

    void stop()
    {
        if (_server) {
            httpd_stop(_server);
            _server = nullptr;
            Serial.println("[MJPEG] stopped");
        }
    }

    void setEnabled(bool on) { _enabled = on; }
    bool isEnabled()   const { return _enabled; }

private:
    static esp_err_t _streamHandler(httpd_req_t* req)
    {
        auto* self = static_cast<MjpegServer*>(req->user_ctx);

        httpd_resp_set_type(req, "multipart/x-mixed-replace; boundary=frame");
        httpd_resp_set_hdr(req, "Access-Control-Allow-Origin", "*");
        httpd_resp_set_hdr(req, "Cache-Control", "no-store, no-cache");
        httpd_resp_set_hdr(req, "Pragma", "no-cache");

        char header[128];
        esp_err_t res = ESP_OK;

        while (res == ESP_OK) {
            if (!self->_enabled) {
                // Stream is paused (stream_off or lobby); wait rather than disconnect
                vTaskDelay(pdMS_TO_TICKS(100));
                continue;
            }

            camera_fb_t* fb = esp_camera_fb_get();
            if (!fb) {
                // Camera not ready yet; brief wait avoids busy-loop
                vTaskDelay(pdMS_TO_TICKS(10));
                continue;
            }

            // MJPEG part header
            int hlen = snprintf(header, sizeof(header),
                "--frame\r\n"
                "Content-Type: image/jpeg\r\n"
                "Content-Length: %u\r\n"
                "\r\n",
                (unsigned)fb->len);

            res = httpd_resp_send_chunk(req, header, hlen);
            if (res == ESP_OK) {
                res = httpd_resp_send_chunk(req, (const char*)fb->buf, fb->len);
            }
            if (res == ESP_OK) {
                res = httpd_resp_send_chunk(req, "\r\n", 2);
            }

            esp_camera_fb_return(fb);
            // No explicit delay; esp_camera_fb_get() naturally paces at the sensor rate
        }

        // Client disconnected (res != ESP_OK) — that's normal; just return
        return ESP_OK;
    }

    httpd_handle_t _server  = nullptr;
    volatile bool  _enabled = false;
};
