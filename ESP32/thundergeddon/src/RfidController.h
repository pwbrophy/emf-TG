// RfidController.h — Minimal MFRC522 driver over I2C (no library required).
//
// Wire must already be initialised (MotorController_PCA9555::begin() does this).
// Default I2C address: 0x28 (AD0-AD2 tied low on the hull expansion board).
//
// poll() returns a text label read from NTAG user data ("north", "centre", "south"),
// or an 8-char uppercase hex UID as fallback.
//
// NTAG213 (the common white NFC sticker) has a 7-byte UID and needs two cascade
// levels of anti-collision + SELECT before a READ command will work.
//
// "text/plain" NDEF records (written by NFC Tools) place the payload text at
// byte 15+ in the user data area, straddling the page 7/8 boundary.  We use
// FAST_READ (0x3A) to grab pages 4-11 (32 bytes) in a single round-trip.

#pragma once
#include <Arduino.h>
#include <Wire.h>

class RfidController
{
    // ---- MFRC522 register addresses ----
    static constexpr uint8_t R_Command    = 0x01;
    static constexpr uint8_t R_ComIrq     = 0x04;
    static constexpr uint8_t R_Error      = 0x06;
    static constexpr uint8_t R_FIFOData   = 0x09;
    static constexpr uint8_t R_FIFOLevel  = 0x0A;
    static constexpr uint8_t R_BitFraming = 0x0D;
    static constexpr uint8_t R_Coll       = 0x0E;
    static constexpr uint8_t R_Mode       = 0x11;
    static constexpr uint8_t R_TxMode     = 0x12;  // bit7 = TxCRCEn
    static constexpr uint8_t R_RxMode     = 0x13;  // bit7 = RxCRCEn
    static constexpr uint8_t R_TxControl  = 0x14;
    static constexpr uint8_t R_TxASK      = 0x15;
    static constexpr uint8_t R_TMode      = 0x2A;
    static constexpr uint8_t R_TPrescaler = 0x2B;
    static constexpr uint8_t R_TReloadH   = 0x2C;
    static constexpr uint8_t R_TReloadL   = 0x2D;
    static constexpr uint8_t R_Version    = 0x37;

    // ---- MFRC522 commands ----
    static constexpr uint8_t CMD_Idle       = 0x00;
    static constexpr uint8_t CMD_Transceive = 0x0C;
    static constexpr uint8_t CMD_SoftReset  = 0x0F;

    // ---- ISO 14443A PICC commands ----
    static constexpr uint8_t PICC_REQA       = 0x26;
    static constexpr uint8_t PICC_SEL_CL1    = 0x93; // cascade level 1 SEL byte
    static constexpr uint8_t PICC_SEL_CL2    = 0x95; // cascade level 2 SEL byte
    static constexpr uint8_t CASCADE_TAG     = 0x88; // level 1 UID[0] when 7-byte UID

    // ---- NTAG commands (require full SELECT first) ----
    static constexpr uint8_t NTAG_FAST_READ = 0x3A; // returns N pages in one shot

    static constexpr uint8_t ADDR = 0x28; // AD0-AD2 = GND

public:
    bool begin()
    {
        uint8_t ver = rd(R_Version);
        if (ver == 0x00 || ver == 0xFF)
        {
            Serial.printf("[RFID] Not found (ver=0x%02X) — skipping\n", ver);
            return false;
        }
        Serial.printf("[RFID] MFRC522 detected (ver=0x%02X)\n", ver);

        wr(R_Command, CMD_SoftReset);
        delay(50);

        wr(R_TMode,      0x8D);
        wr(R_TPrescaler, 0x3E);
        wr(R_TReloadH,   0x00);
        wr(R_TReloadL,   0x19); // ~25 ms timeout

        wr(R_TxASK, 0x40); // 100% ASK modulation
        wr(R_Mode,  0x3D); // CRC preset 0x6363

        uint8_t tx = rd(R_TxControl);
        if (!(tx & 0x03)) wr(R_TxControl, tx | 0x03);

        _ok = true;
        return true;
    }

    // Non-blocking poll.  Returns a text label (e.g. "north") if found in NTAG user
    // data, or an 8-char uppercase hex UID as fallback.  Returns "" if no new card.
    String poll(uint32_t now)
    {
        if (!_ok) return "";
        if (now - _lastPoll < 200) return ""; // max 5 Hz
        _lastPoll = now;

        uint8_t atqa[2];
        if (!sendShort(PICC_REQA, atqa, 2))
        {
            // Don't clear _lastUid immediately — MIFARE Classic stays ACTIVE after
            // SELECT and won't respond to REQA for ~300 ms before auto-resetting.
            // Wait for 5 consecutive failures (~1 s) before treating the card as gone.
            if (++_reqaFailCount >= 5) { _lastUid = ""; _reqaFailCount = 0; }
            return "";
        }
        _reqaFailCount = 0;

        // Level-1 anti-collision (NVB=0x20 = 0 known UID bits, no CRC)
        uint8_t req1[2] = { PICC_SEL_CL1, 0x20 };
        uint8_t cl1[5];  // UID[0..3] + BCC
        if (!sendFull(req1, 2, cl1, 5)) return "";

        // Build hex UID for dedup (uses CL1 bytes; good enough as a unique key)
        char hexBuf[9];
        snprintf(hexBuf, sizeof(hexBuf), "%02X%02X%02X%02X",
                 cl1[0], cl1[1], cl1[2], cl1[3]);
        String uidHex(hexBuf);

        if (uidHex == _lastUid) return "";

        // Try to fully select the card and read a text label from NTAG user data
        String label = tryReadLabel(cl1);

        String key = (label.length() > 0) ? label : uidHex;
        if (key == _lastUid) return "";

        _lastUid = key;
        Serial.printf("[RFID] Tag: %s\n", key.c_str());
        return key;
    }

private:
    bool     _ok           = false;
    uint32_t _lastPoll     = 0;
    String   _lastUid;
    uint8_t  _reqaFailCount = 0; // consecutive REQA failures; card gone after 5 (~1 s)

    // ---- I2C register helpers ----

    void wr(uint8_t reg, uint8_t val)
    {
        Wire.beginTransmission(ADDR);
        Wire.write(reg); Wire.write(val);
        Wire.endTransmission();
    }

    uint8_t rd(uint8_t reg)
    {
        Wire.beginTransmission(ADDR);
        Wire.write(reg);
        if (Wire.endTransmission(false) != 0) return 0xFF;
        Wire.requestFrom(ADDR, (uint8_t)1);
        return Wire.available() ? Wire.read() : 0xFF;
    }

    void setBits(uint8_t reg, uint8_t mask) { wr(reg, rd(reg) |  mask); }
    void clrBits(uint8_t reg, uint8_t mask) { wr(reg, rd(reg) & ~mask); }

    // ---- Transceive helpers ----

    // 7-bit short frame (REQA/WUPA) — no CRC
    bool sendShort(uint8_t cmd, uint8_t* rx, uint8_t rxLen)
    {
        clrBits(R_Coll, 0x80);
        wr(R_Command,    CMD_Idle);
        wr(R_ComIrq,     0x7F);
        setBits(R_FIFOLevel, 0x80);
        wr(R_FIFOData,   cmd);
        wr(R_BitFraming, 0x07); // TxLastBits = 7
        wr(R_Command,    CMD_Transceive);
        setBits(R_BitFraming, 0x80); // StartSend
        return waitAndRead(rx, rxLen);
    }

    // Full-byte transceive, no CRC (anti-collision commands)
    bool sendFull(uint8_t* tx, uint8_t txLen, uint8_t* rx, uint8_t rxLen)
    {
        wr(R_Command,    CMD_Idle);
        wr(R_ComIrq,     0x7F);
        setBits(R_FIFOLevel, 0x80);
        for (uint8_t i = 0; i < txLen; i++) wr(R_FIFOData, tx[i]);
        wr(R_BitFraming, 0x00);
        wr(R_Command,    CMD_Transceive);
        setBits(R_BitFraming, 0x80);
        return waitAndRead(rx, rxLen);
    }

    // Full-byte transceive with hardware CRC appended to TX and stripped/checked on RX.
    // Used for SELECT and NTAG READ where the protocol requires CRC.
    bool sendWithCRC(uint8_t* tx, uint8_t txLen, uint8_t* rx, uint8_t rxLen)
    {
        setBits(R_TxMode, 0x80); // TxCRCEn
        setBits(R_RxMode, 0x80); // RxCRCEn — hardware strips 2 CRC bytes from reply
        bool ok = sendFull(tx, txLen, rx, rxLen);
        clrBits(R_TxMode, 0x80);
        clrBits(R_RxMode, 0x80);
        if (ok && (rd(R_Error) & 0x04)) ok = false; // reject on CRC error
        return ok;
    }

    bool waitAndRead(uint8_t* rx, uint8_t rxLen)
    {
        uint32_t deadline = millis() + 30;
        while ((int32_t)(millis() - deadline) < 0)
        {
            uint8_t irq = rd(R_ComIrq);
            if (irq & 0x30) break;        // RxIrq or IdleIrq
            if (irq & 0x01) return false; // TimerIrq — no card
        }
        if (rd(R_Error) & 0x17) return false; // overflow/parity/CRC/protocol
        uint8_t n = rd(R_FIFOLevel);
        if (n < rxLen) return false;
        for (uint8_t i = 0; i < rxLen; i++) rx[i] = rd(R_FIFOData);
        return true;
    }

    // ---- Full SELECT + NTAG read ----

    // cl1[0..4]: level-1 anti-collision response (UID bytes 0-3 + BCC).
    //
    // NTAG213 has a 7-byte UID.  Level-1 returns [0x88, uid1, uid2, uid3, bcc1]
    // where 0x88 is the cascade tag (CT).  After level-1 SELECT the SAK has bit 2
    // set (UID not complete), so we must do level-2 anti-collision + SELECT to
    // reach the ACTIVE state before any READ will work.
    //
    // After full SELECT we issue FAST_READ pages 4-11 (32 bytes) which covers the
    // complete NDEF TLV even for MIME "text/plain" records where the payload starts
    // at byte 15 (straddling the page 7/8 boundary).
    String tryReadLabel(uint8_t* cl1)
    {
        // ── Level-1 SELECT ──────────────────────────────────────────────
        uint8_t bcc1 = cl1[0] ^ cl1[1] ^ cl1[2] ^ cl1[3];
        uint8_t sel1[7] = { PICC_SEL_CL1, 0x70, cl1[0], cl1[1], cl1[2], cl1[3], bcc1 };
        uint8_t sak1[1];
        if (!sendWithCRC(sel1, 7, sak1, 1)) return "";

        if (sak1[0] & 0x04)
        {
            // UID not complete — need cascade level 2 (NTAG213 always hits this path)
            uint8_t req2[2] = { PICC_SEL_CL2, 0x20 };
            uint8_t cl2[5]; // UID bytes 3-6 + BCC2
            if (!sendFull(req2, 2, cl2, 5)) return "";

            uint8_t bcc2 = cl2[0] ^ cl2[1] ^ cl2[2] ^ cl2[3];
            uint8_t sel2[7] = { PICC_SEL_CL2, 0x70, cl2[0], cl2[1], cl2[2], cl2[3], bcc2 };
            uint8_t sak2[1];
            if (!sendWithCRC(sel2, 7, sak2, 1)) return "";

            if (sak2[0] != 0x00) return ""; // not NFC Type 2 — no data read
        }
        else if (sak1[0] != 0x00)
        {
            return ""; // MIFARE Classic (SAK=0x08) or other — no data read
        }

        // ── Card is now fully ACTIVE — FAST_READ pages 4-11 (32 bytes) ──
        // "text/plain" NDEF "north": header bytes fill pages 4-7, payload "north"
        // starts at byte 15 and ends at byte 19, entirely within this 32-byte window.
        uint8_t fastRead[3] = { NTAG_FAST_READ, 4, 11 };
        uint8_t data[32];
        if (!sendWithCRC(fastRead, 3, data, 32))
        {
            // Older/non-NTAG tags: fall back to single-page READ
            uint8_t readCmd[2] = { 0x30, 4 };
            uint8_t page[16];
            if (!sendWithCRC(readCmd, 2, page, 16)) return "";
            return findLabel(page, 16);
        }

        return findLabel(data, 32);
    }

    // Scan buf for the keyword sequences "north", "centre", "south" (ASCII, case-insensitive).
    static String findLabel(const uint8_t* buf, uint8_t len)
    {
        if (containsWord(buf, len, "north"))  return "north";
        if (containsWord(buf, len, "centre")) return "centre";
        if (containsWord(buf, len, "south"))  return "south";
        return "";
    }

    static bool containsWord(const uint8_t* buf, uint8_t len, const char* word)
    {
        uint8_t wlen = (uint8_t)strlen(word);
        if (wlen > len) return false;
        for (uint8_t i = 0; i <= len - wlen; i++)
        {
            bool match = true;
            for (uint8_t j = 0; j < wlen; j++)
            {
                uint8_t c = buf[i + j];
                if (c >= 'A' && c <= 'Z') c |= 0x20; // to lower
                if (c != (uint8_t)word[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
};
