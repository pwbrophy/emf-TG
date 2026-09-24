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
#include "lwip/sockets.h"

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
        cfg.max_open_sockets = 3;     // player phone + spectator display + spare slot

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

    // Cap the streamed frame rate. fps <= 0 = uncapped; fps > 30 clamped to 30.
    // Default is 20 fps (set on the member). The streaming task paces itself to
    // this interval so it never emits frames faster than requested — this is the
    // main lever for keeping aggregate Wi-Fi bandwidth manageable with many robots.
    void setMaxFps(int fps)
    {
        if (fps <= 0)       _minFrameIntervalMs = 0;
        else if (fps > 30)  _minFrameIntervalMs = 1000u / 30u;
        else                _minFrameIntervalMs = 1000u / (uint32_t)fps;
    }

private:
    // Writes every byte of the iovec list, retrying partial writes. False if the
    // client has gone (or stalled past httpd's 5 s send timeout).
    static bool _writevAll(int fd, struct iovec* iov, int cnt)
    {
        while (cnt > 0) {
            ssize_t n = lwip_writev(fd, iov, cnt);
            if (n <= 0) return false;
            while (cnt > 0 && (size_t)n >= iov->iov_len) { n -= iov->iov_len; ++iov; --cnt; }
            if (cnt > 0) {
                iov->iov_base = (char*)iov->iov_base + n;
                iov->iov_len -= n;
            }
        }
        return true;
    }

    // The stream is written straight to the socket rather than through
    // httpd_resp_send_chunk(). Chunked encoding split every frame into nine
    // small socket writes, and with Nagle on, the tail of each JPEG then sat in
    // the robot until the viewer ACKed the rest — up to 200 ms on a Windows
    // receiver (the web server's stream proxy). Now each frame is a single
    // writev() of part header + JPEG + closing boundary with TCP_NODELAY set:
    // full-size segments, no tiny packets, and nothing held back.
    //
    // Each part ends with the NEXT boundary line: browsers only display a
    // multipart part once they see the boundary that closes it, so sending it
    // straight after the JPEG shows the frame one frame-interval sooner.
    static esp_err_t _streamHandler(httpd_req_t* req)
    {
        auto* self = static_cast<MjpegServer*>(req->user_ctx);
        const int fd = httpd_req_to_sockfd(req);

        int one = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));

        static const char kResponseHead[] =
            "HTTP/1.1 200 OK\r\n"
            "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n"
            "Access-Control-Allow-Origin: *\r\n"
            "Cache-Control: no-store, no-cache\r\n"
            "Pragma: no-cache\r\n"
            "Connection: close\r\n"
            "\r\n"
            "--frame\r\n";
        static const char kEndBoundary[] = "\r\n--frame\r\n";

        struct iovec head = { (void*)kResponseHead, sizeof(kResponseHead) - 1 };
        if (!_writevAll(fd, &head, 1)) return ESP_FAIL;

        char header[96];
        uint32_t lastFrameMs = 0;

        for (;;) {
            if (!self->_enabled) {
                // Stream is paused (stream_off or lobby); wait rather than disconnect
                vTaskDelay(pdMS_TO_TICKS(100));
                continue;
            }

            // Frame-rate cap: wait until the minimum inter-frame interval elapses.
            uint32_t interval = self->_minFrameIntervalMs;
            if (interval > 0) {
                uint32_t since = millis() - lastFrameMs;
                if (since < interval) vTaskDelay(pdMS_TO_TICKS(interval - since));
                lastFrameMs = millis();
            }

            camera_fb_t* fb = esp_camera_fb_get();
            if (!fb) {
                // Camera not ready yet; brief wait avoids busy-loop
                vTaskDelay(pdMS_TO_TICKS(10));
                continue;
            }

            // MJPEG part: header, JPEG, then the boundary that closes this part
            int hlen = snprintf(header, sizeof(header),
                "Content-Type: image/jpeg\r\n"
                "Content-Length: %u\r\n"
                "\r\n",
                (unsigned)fb->len);

            struct iovec iov[3] = {
                { header,              (size_t)hlen },
                { fb->buf,             fb->len },
                { (void*)kEndBoundary, sizeof(kEndBoundary) - 1 },
            };
            bool ok = _writevAll(fd, iov, 3);

            esp_camera_fb_return(fb);
            if (!ok) break;
            // No explicit delay; esp_camera_fb_get() naturally paces at the sensor rate
        }

        // Client disconnected — normal. ESP_FAIL tells httpd to close the socket
        // (the response was hand-written, so it can't be reused for keep-alive).
        return ESP_FAIL;
    }

    httpd_handle_t   _server  = nullptr;
    volatile bool    _enabled = false;
    volatile uint32_t _minFrameIntervalMs = 50; // 50 ms = 20 fps default cap
};
