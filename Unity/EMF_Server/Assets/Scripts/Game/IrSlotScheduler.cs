using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Manages the IR fire-slot queue and result collection.
// Registered as ServiceLocator.IrSlotScheduler in Awake.
//
// One slot executes at a time (_busy gate). Incoming EnqueueFire calls queue
// behind any active slot. The coroutine:
//   1. Validates shooter + builds enemy list
//   2. Sends time_sync to all connected robots
//   3. Sends ir_fire_slot to the shooter and ir_listen_slot to each enemy
//   4. Waits for slot end + result buffer (wall-clock based, not WS acks)
//   5. Resolves hits: b1Mask & b2Mask per enemy; any bit set in both = valid hit
//   6. Applies damage, flash_hit (red LEDs + buzzer), set_hp for each confirmed hit
public class IrSlotScheduler : MonoBehaviour
{
    private readonly Queue<string> _queue = new Queue<string>();
    private bool _busy;
    private int  _nextSlotId = 1;

    private int _currentSlotId;
    private readonly Dictionary<string, (byte b1, byte b2)> _slotResults =
        new Dictionary<string, (byte, byte)>();

    private void Awake()
    {
        ServiceLocator.IrSlotScheduler = this;
    }

    private void OnDestroy()
    {
        if (ServiceLocator.IrSlotScheduler == this)
            ServiceLocator.IrSlotScheduler = null;
    }

    public void EnqueueFire(string shooterId)
    {
        if (string.IsNullOrEmpty(shooterId)) return;
        _queue.Enqueue(shooterId);
        Debug.Log($"[IrSlot] >>> FIRE REQUEST queued for {shooterId} (queue depth: {_queue.Count})");
    }

    private void Update()
    {
        if (_busy || _queue.Count == 0) return;
        string next = _queue.Dequeue();
        var settings = ServiceLocator.GameSettings;
        StartCoroutine(settings != null && settings.UseHandshakeIr
            ? ExecuteSlotHandshake(next)
            : ExecuteSlot(next));
    }

    private IEnumerator ExecuteSlot(string shooterId)
    {
        _busy = true;
        int slotId = _nextSlotId++;

        Debug.Log($"[IrSlot] ============ SLOT {slotId} START — shooter={shooterId} ============");

        var server   = ServiceLocator.RobotServer;
        var dir      = ServiceLocator.RobotDirectory;
        var players  = ServiceLocator.Players;
        var game     = ServiceLocator.Game;
        var settings = ServiceLocator.GameSettings;

        // ── Validate prerequisites ─────────────────────────────────────────
        if (server == null || dir == null || players == null)
        {
            Debug.LogError($"[IrSlot] Slot {slotId} ABORTED — missing services: " +
                           $"server={server != null} dir={dir != null} players={players != null}");
            _busy = false;
            yield break;
        }

        if (game?.State == null)
            Debug.LogWarning($"[IrSlot] Slot {slotId} — game.State is null (game not started?); " +
                             "IR detection will run but damage will NOT be applied.");

        if (!dir.TryGet(shooterId, out var shooterInfo))
        {
            Debug.LogWarning($"[IrSlot] Slot {slotId} ABORTED — shooter {shooterId} not in directory.");
            _busy = false;
            yield break;
        }

        var playerList = players.GetAll();
        int GetAllianceIndex(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return -1;
            for (int i = 0; i < playerList.Count; i++)
                if (playerList[i].Name == playerName) return playerList[i].AllianceIndex;
            return -1;
        }

        int shooterAlliance = GetAllianceIndex(shooterInfo.AssignedPlayer);
        Debug.Log($"[IrSlot] Slot {slotId} — shooter={shooterInfo.Callsign ?? shooterId} " +
                  $"player={shooterInfo.AssignedPlayer ?? "(none)"} alliance={shooterAlliance}");

        if (shooterAlliance < 0)
        {
            Debug.LogWarning($"[IrSlot] Slot {slotId} ABORTED — shooter has no valid alliance.");
            _busy = false;
            yield break;
        }

        // Build enemy list
        var allRobots = dir.GetAll();
        var enemies   = new List<RobotInfo>();
        var deadSet   = game?.State?.DeadRobots;

        for (int i = 0; i < allRobots.Count; i++)
        {
            var r = allRobots[i];
            if (r.RobotId == shooterId) continue;
            if (string.IsNullOrEmpty(r.AssignedPlayer)) continue;
            if (deadSet != null && deadSet.Contains(r.RobotId)) { Debug.Log($"[IrSlot]   skip {r.RobotId} (dead)"); continue; }
            int ally = GetAllianceIndex(r.AssignedPlayer);
            if (ally < 0 || ally == shooterAlliance) { Debug.Log($"[IrSlot]   skip {r.RobotId} (ally={ally}, same team or unassigned)"); continue; }
            Debug.Log($"[IrSlot]   enemy: {r.Callsign ?? r.RobotId} (alliance {ally})");
            enemies.Add(r);
        }

        if (enemies.Count == 0)
        {
            Debug.LogWarning($"[IrSlot] Slot {slotId} ABORTED — no valid enemies found.");
            _busy = false;
            yield break;
        }

        Debug.Log($"[IrSlot] Slot {slotId} — {enemies.Count} enemy(ies) targeted.");

        // ── Compute slot timing (read live from GameSettings so UI changes take effect immediately) ──
        int   slotFutureMs        = settings != null ? settings.SlotFutureMs       : 300;
        int   b1DurMs             = settings != null ? settings.B1DurMs            : 25;
        int   gap12Ms             = settings != null ? settings.Gap12Ms            : 20;
        int   b2DurMs             = settings != null ? settings.B2DurMs            : 25;
        int   repGapMs            = settings != null ? settings.RepGapMs           : 20;
        int   reps                = settings != null ? settings.Reps               : 7;
        float resultBufferSeconds = settings != null ? settings.ResultBufferSeconds : 0.5f;

        int   delayMs   = slotFutureMs;
        int   perRepMs  = b1DurMs + gap12Ms + b2DurMs + repGapMs;
        int   slotDurMs = reps * perRepMs - repGapMs;

        Debug.Log($"[IrSlot] Slot {slotId} timing: " +
                  $"delay={delayMs}ms | dur={slotDurMs}ms | " +
                  $"reps={reps} | b1={b1DurMs}ms gap={gap12Ms}ms b2={b2DurMs}ms repGap={repGapMs}ms");
        Debug.Log($"[IrSlot] Slot {slotId} windows (relative to command receipt): " +
                  $"Burst1=[{delayMs}..{delayMs+b1DurMs}ms] Burst2=[{delayMs+b1DurMs+gap12Ms}..{delayMs+b1DurMs+gap12Ms+b2DurMs}ms] " +
                  $"(per rep, period={perRepMs}ms)");

        _currentSlotId = slotId;
        _slotResults.Clear();

        // ── Register result handler ────────────────────────────────────────
        void OnResult(string robotId, int sid, byte b1, byte b2)
        {
            if (sid != _currentSlotId) return;
            _slotResults[robotId] = (b1, b2);
            Debug.Log($"[IrSlot] <<< RESULT from {robotId} slot={sid}: " +
                      $"b1=0x{b1:X2} ({MaskToDirs(b1)}) " +
                      $"b2=0x{b2:X2} ({MaskToDirs(b2)}) " +
                      $"both=0x{(byte)(b1 & b2):X2} ({MaskToDirs((byte)(b1 & b2))})");
        }
        server.OnIrSlotResult += OnResult;

        // ── Pause cameras, stop motors, sync clocks, send fire/listen commands ──
        bool disableCam    = settings != null && settings.DisableCameraWhileDetecting;
        bool disableMotors = settings != null && settings.DisableMotorsWhileDetecting;

        HashSet<string> wasStreaming = disableCam ? server.PauseAllStreams() : null;

        if (disableMotors)
        {
            server.SendMotorsOff(shooterId);
            for (int i = 0; i < enemies.Count; i++)
                server.SendMotorsOff(enemies[i].RobotId);
            Debug.Log($"[IrSlot] Slot {slotId} — motors OFF for shooter + {enemies.Count} enemy(ies)");
        }

        long unityMs     = (long)(Time.unscaledTime * 1000.0);
        server.SendTimeSyncAll(unityMs);
        long slotStartUt = unityMs + delayMs;

        bool fireSent = server.SendIrFireSlot(shooterId, slotId, slotStartUt,
                                              b1DurMs, gap12Ms, b2DurMs, repGapMs, reps);
        Debug.Log($"[IrSlot] Slot {slotId} — ir_fire_slot to {shooterId}: {(fireSent ? "OK" : "FAILED")}");

        for (int i = 0; i < enemies.Count; i++)
        {
            bool listenSent = server.SendIrListenSlot(enemies[i].RobotId, slotId, slotStartUt,
                                                      b1DurMs, gap12Ms, b2DurMs, repGapMs, reps);
            Debug.Log($"[IrSlot] Slot {slotId} — ir_listen_slot to {enemies[i].Callsign ?? enemies[i].RobotId}: {(listenSent ? "OK" : "FAILED")}");
        }

        // ── Wait for slot end + result buffer ─────────────────────────────
        float waitUntil = Time.time + (delayMs + slotDurMs) / 1000f + resultBufferSeconds;
        float waitSecs  = waitUntil - Time.time;
        Debug.Log($"[IrSlot] Slot {slotId} — waiting up to {waitSecs * 1000f:F0}ms for results...");
        while (Time.time < waitUntil && _slotResults.Count < enemies.Count) yield return null;
        Debug.Log($"[IrSlot] Slot {slotId} — wait ended: {_slotResults.Count}/{enemies.Count} results, " +
                  $"{(waitUntil - Time.time) * 1000f:F0}ms remaining in window");

        server.OnIrSlotResult -= OnResult;

        // ── Resolve hits ───────────────────────────────────────────────────
        Debug.Log($"[IrSlot] ---- Slot {slotId} RESOLVING ({_slotResults.Count}/{enemies.Count} results received) ----");

        int maxHp   = settings != null ? settings.MaxHp : 100;
        int hitCount = 0;

        for (int i = 0; i < enemies.Count; i++)
        {
            string enemyId      = enemies[i].RobotId;
            string enemyName    = enemies[i].Callsign ?? enemyId;

            if (!_slotResults.TryGetValue(enemyId, out var masks))
            {
                Debug.Log($"[IrSlot]   {enemyName}: NO RESULT (robot didn't respond in time)");
                continue;
            }

            // AND across both burst windows: hit requires detection of BOTH IR LEDs
            // (LED1 fires in burst 1, LED2 fires in burst 2). Filters ambient IR and
            // single-window reflections — only a direct beam triggers both windows.
            byte detMask = (byte)(masks.b1 & masks.b2);
            Debug.Log($"[IrSlot]   {enemyName}: b1=0x{masks.b1:X2} b2=0x{masks.b2:X2} " +
                      $"det=0x{detMask:X2} ({MaskToDirs(detMask)}) → {(detMask != 0 ? "HIT" : "miss")}");

            if (detMask == 0)
                continue;

            string cardinalDir = ResolveAveragedCardinal(detMask);
            bool isRear        = cardinalDir == "S";
            Debug.Log($"[IrSlot] *** HIT: {enemyName} sensors={MaskToDirs(detMask)} " +
                      $"cardinal={cardinalDir} {(isRear ? "(REAR — 3× damage!)" : "")} ***");
            hitCount++;

            // Skip dead robots — ApplyDamage also guards this, but we must not
            // send flash_hit to a robot that is already dead (would play buzzer + red LEDs).
            var state = game?.State;
            if (state != null && (state.DeadRobots.Contains(enemyId) || state.RespawningRobots.Contains(enemyId)))
            {
                Debug.Log($"[IrSlot]   {enemyName} is dead/respawning — skipping hit effects");
                continue;
            }

            // Apply damage (also fires OnHpChanged → PlayerWebSocketServer sends state_update to phone)
            int damage = 0;
            if (game != null)
                damage = game.ApplyDamage(shooterId, enemyId, detMask, cardinalDir, players, dir);

            int newHp = game?.State?.RobotHp.GetValueOrDefault(enemyId, 0) ?? 0;
            Debug.Log($"[IrSlot]   damage={damage} newHp={newHp}/{maxHp}");

            // Flash red + play damage buzzer only if damage was actually dealt
            if (damage > 0)
                server.SendFlashHit(enemyId);

            // Update HP bar LEDs on hit robot
            server.SendSetHp(enemyId, newHp, maxHp);
        }

        Debug.Log($"[IrSlot] ============ SLOT {slotId} DONE — {hitCount} hit(s) of {enemies.Count} enemy(ies) ============");

        if (disableMotors)
        {
            server.SendMotorsOn(shooterId);
            for (int i = 0; i < enemies.Count; i++)
                server.SendMotorsOn(enemies[i].RobotId);
            Debug.Log($"[IrSlot] Slot {slotId} — motors ON restored");
        }

        if (wasStreaming != null) server.RestoreStreams(wasStreaming);
        _busy = false;
    }

    // ── Handshake coroutine ───────────────────────────────────────────────────
    //
    // ACK-driven alternative to ExecuteSlot. No clock sync needed — the server
    // waits for explicit confirmations at each step, so timing adapts to actual
    // Wi-Fi latency rather than relying on a fixed lookahead delay.
    //
    // Sequence:
    //   1. ir_emit_left  → wait ir_emit_ack from shooter
    //   2. ir_listen_window → wait ir_window_result from all enemies  (b1 masks)
    //   3. Early exit if all b1 == 0
    //   4. ir_emit_right → wait ir_emit_ack from shooter
    //   5. ir_listen_window → wait ir_window_result from all enemies  (b2 masks)
    //   6. ir_emit_stop to shooter
    //   7. Resolve hits: (b1 & b2) != 0, same logic as ExecuteSlot
    private IEnumerator ExecuteSlotHandshake(string shooterId)
    {
        _busy = true;
        int slotId = _nextSlotId++;

        Debug.Log($"[IrHs] ===== HANDSHAKE SHOT {slotId} START — shooter={shooterId} =====");

        var server   = ServiceLocator.RobotServer;
        var dir      = ServiceLocator.RobotDirectory;
        var players  = ServiceLocator.Players;
        var game     = ServiceLocator.Game;
        var settings = ServiceLocator.GameSettings;

        if (server == null || dir == null || players == null)
        {
            Debug.LogError($"[IrHs] Shot {slotId} ABORTED — missing services");
            _busy = false;
            yield break;
        }

        if (!dir.TryGet(shooterId, out var shooterInfo))
        {
            Debug.LogWarning($"[IrHs] Shot {slotId} ABORTED — shooter {shooterId} not in directory");
            _busy = false;
            yield break;
        }

        var playerList = players.GetAll();
        int GetAllianceIndex(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return -1;
            for (int i = 0; i < playerList.Count; i++)
                if (playerList[i].Name == playerName) return playerList[i].AllianceIndex;
            return -1;
        }

        int shooterAlliance = GetAllianceIndex(shooterInfo.AssignedPlayer);
        if (shooterAlliance < 0)
        {
            Debug.LogWarning($"[IrHs] Shot {slotId} ABORTED — shooter has no valid alliance");
            _busy = false;
            yield break;
        }

        var allRobots = dir.GetAll();
        var enemies   = new List<RobotInfo>();
        var deadSet   = game?.State?.DeadRobots;

        for (int i = 0; i < allRobots.Count; i++)
        {
            var r = allRobots[i];
            if (r.RobotId == shooterId) continue;
            if (string.IsNullOrEmpty(r.AssignedPlayer)) continue;
            if (deadSet != null && deadSet.Contains(r.RobotId)) continue;
            int ally = GetAllianceIndex(r.AssignedPlayer);
            if (ally < 0 || ally == shooterAlliance) continue;
            Debug.Log($"[IrHs]   enemy: {r.Callsign ?? r.RobotId}");
            enemies.Add(r);
        }

        if (enemies.Count == 0)
        {
            Debug.LogWarning($"[IrHs] Shot {slotId} ABORTED — no valid enemies");
            _busy = false;
            yield break;
        }

        int ackTimeoutMs    = settings != null ? settings.HandshakeAckTimeoutMs    : 300;
        int windowMs        = settings != null ? settings.HandshakeWindowMs        : 100;
        int windowTimeoutMs = settings != null ? settings.HandshakeWindowTimeoutMs : 300;
        bool disableCam     = settings != null && settings.DisableCameraWhileDetecting;
        bool disableMotors  = settings != null && settings.DisableMotorsWhileDetecting;

        HashSet<string> wasStreaming = disableCam ? server.PauseAllStreams() : null;

        // Motors: the shooter disables its own motors when it receives ir_emit_left.
        // Optionally disable enemy motors to reduce electrical noise on their receivers.
        if (disableMotors)
            for (int i = 0; i < enemies.Count; i++)
                server.SendMotorsOff(enemies[i].RobotId);

        // ── Track ACK and window results via events ────────────────────────────
        bool  ackReceived  = false;
        bool  collectingB2 = false;
        var   b1Masks = new Dictionary<string, byte>();
        var   b2Masks = new Dictionary<string, byte>();

        void OnAck(string robotId)
        {
            if (robotId == shooterId) ackReceived = true;
        }
        void OnWindow(string robotId, byte mask)
        {
            bool isEnemy = false;
            for (int i = 0; i < enemies.Count; i++)
                if (enemies[i].RobotId == robotId) { isEnemy = true; break; }
            if (!isEnemy) return;

            if (!collectingB2)
                b1Masks[robotId] = mask;
            else
                b2Masks[robotId] = mask;

            Debug.Log($"[IrHs]   window result from {robotId}: mask=0x{mask:X2} ({(collectingB2 ? "b2" : "b1")})");
        }

        server.OnIrEmitAck      += OnAck;
        server.OnIrWindowResult += OnWindow;

        bool aborted = false;

        // ── Phase 1: left LED ─────────────────────────────────────────────────
        ackReceived = false;
        server.SendIrEmitLeft(shooterId);
        Debug.Log($"[IrHs] Shot {slotId} — ir_emit_left sent, waiting for ack ({ackTimeoutMs}ms)...");

        float ackDeadline = Time.time + ackTimeoutMs / 1000f;
        while (!ackReceived && Time.time < ackDeadline) yield return null;

        if (!ackReceived)
        {
            Debug.LogWarning($"[IrHs] Shot {slotId} ABORTED — no ack for ir_emit_left (timeout)");
            server.SendIrEmitStop(shooterId);
            aborted = true;
        }
        else
        {
            Debug.Log($"[IrHs] Shot {slotId} — ir_emit_ack received; sending ir_listen_window to {enemies.Count} enemy(ies)");
            for (int i = 0; i < enemies.Count; i++)
                server.SendIrListenWindow(enemies[i].RobotId, windowMs);

            float winDeadline = Time.time + (windowMs + windowTimeoutMs) / 1000f;
            while (b1Masks.Count < enemies.Count && Time.time < winDeadline) yield return null;
            Debug.Log($"[IrHs] Shot {slotId} — b1 done: {b1Masks.Count}/{enemies.Count} results");

            // Early exit: if no enemy detected the left LED, b2 can't possibly count
            bool anyB1Hit = false;
            foreach (var kv in b1Masks) if (kv.Value != 0) { anyB1Hit = true; break; }
            if (!anyB1Hit && b1Masks.Count > 0)
            {
                Debug.Log($"[IrHs] Shot {slotId} — all b1 == 0, early exit (miss)");
                server.SendIrEmitStop(shooterId);
                aborted = true;
            }
        }

        // ── Phase 2: right LED ────────────────────────────────────────────────
        if (!aborted)
        {
            collectingB2 = true;
            ackReceived  = false;
            server.SendIrEmitRight(shooterId);
            Debug.Log($"[IrHs] Shot {slotId} — ir_emit_right sent, waiting for ack...");

            ackDeadline = Time.time + ackTimeoutMs / 1000f;
            while (!ackReceived && Time.time < ackDeadline) yield return null;

            if (!ackReceived)
            {
                Debug.LogWarning($"[IrHs] Shot {slotId} ABORTED — no ack for ir_emit_right (timeout)");
                server.SendIrEmitStop(shooterId);
                aborted = true;
            }
            else
            {
                Debug.Log($"[IrHs] Shot {slotId} — ir_emit_ack received; sending ir_listen_window for b2");
                for (int i = 0; i < enemies.Count; i++)
                    server.SendIrListenWindow(enemies[i].RobotId, windowMs);

                float winDeadline = Time.time + (windowMs + windowTimeoutMs) / 1000f;
                while (b2Masks.Count < enemies.Count && Time.time < winDeadline) yield return null;
                Debug.Log($"[IrHs] Shot {slotId} — b2 done: {b2Masks.Count}/{enemies.Count} results");

                server.SendIrEmitStop(shooterId);
            }
        }

        // ── Resolve hits ──────────────────────────────────────────────────────
        if (!aborted)
        {
            int maxHp    = settings != null ? settings.MaxHp : 100;
            int hitCount = 0;

            Debug.Log($"[IrHs] ---- Shot {slotId} RESOLVING ----");
            for (int i = 0; i < enemies.Count; i++)
            {
                string enemyId   = enemies[i].RobotId;
                string enemyName = enemies[i].Callsign ?? enemyId;

                b1Masks.TryGetValue(enemyId, out byte b1);
                b2Masks.TryGetValue(enemyId, out byte b2);

                byte detMask = (byte)(b1 & b2);
                Debug.Log($"[IrHs]   {enemyName}: b1=0x{b1:X2} b2=0x{b2:X2} det=0x{detMask:X2} " +
                           $"({MaskToDirs(detMask)}) → {(detMask != 0 ? "HIT" : "miss")}");

                if (detMask == 0) continue;

                string cardinalDir = ResolveAveragedCardinal(detMask);
                bool   isRear      = cardinalDir == "S";
                Debug.Log($"[IrHs] *** HIT: {enemyName} cardinal={cardinalDir} {(isRear ? "(REAR — 3×)" : "")} ***");
                hitCount++;

                var state = game?.State;
                if (state != null && (state.DeadRobots.Contains(enemyId) || state.RespawningRobots.Contains(enemyId)))
                {
                    Debug.Log($"[IrHs]   {enemyName} is dead/respawning — skipping hit effects");
                    continue;
                }

                int damage = 0;
                if (game != null)
                    damage = game.ApplyDamage(shooterId, enemyId, detMask, cardinalDir, players, dir);

                int newHp = game?.State?.RobotHp.GetValueOrDefault(enemyId, 0) ?? 0;
                Debug.Log($"[IrHs]   damage={damage} newHp={newHp}/{maxHp}");

                if (damage > 0)
                    server.SendFlashHit(enemyId);

                server.SendSetHp(enemyId, newHp, maxHp);
            }
            Debug.Log($"[IrHs] ===== Shot {slotId} DONE — {hitCount} hit(s) of {enemies.Count} =====");
        }
        else
        {
            Debug.Log($"[IrHs] ===== Shot {slotId} DONE — aborted, no hits applied =====");
        }

        server.OnIrEmitAck      -= OnAck;
        server.OnIrWindowResult -= OnWindow;

        if (disableMotors)
            for (int i = 0; i < enemies.Count; i++)
                server.SendMotorsOn(enemies[i].RobotId);

        if (wasStreaming != null) server.RestoreStreams(wasStreaming);
        _busy = false;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static readonly string[] DirNames  = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    private static readonly float[]  DirAngles = { 90f, 45f, 0f, -45f, -90f, -135f, 180f, 135f };

    // Circular mean of all hit sensors snapped to the nearest cardinal (N/E/S/W).
    // Tie-break priority: N > (E,W) > S — so NE→N, NW→N, SE→E, SW→W.
    private static string ResolveAveragedCardinal(byte mask)
    {
        if (mask == 0) return "N";

        float sinSum = 0f, cosSum = 0f;
        for (int i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            float rad = DirAngles[i] * Mathf.Deg2Rad;
            sinSum += Mathf.Sin(rad);
            cosSum += Mathf.Cos(rad);
        }

        // Zero vector (e.g. N+S or E+W cancel) → default North
        if (Mathf.Approximately(sinSum, 0f) && Mathf.Approximately(cosSum, 0f))
            return "N";

        float meanDeg = Mathf.Atan2(sinSum, cosSum) * Mathf.Rad2Deg;
        if (meanDeg < 0f) meanDeg += 360f;  // normalise to [0, 360)

        // Boundaries chosen so ties favour front:
        //   [45, 135]  → N  (NE=45° and NW=135° both → N)
        //   (135, 225] → W  (SW=225° → W, not S)
        //   (225, 315) → S
        //   [0,45) ∪ [315,360) → E  (SE=315° → E)
        if (meanDeg >= 45f && meanDeg <= 135f)  return "N";
        if (meanDeg > 135f && meanDeg <= 225f)  return "W";
        if (meanDeg > 225f && meanDeg < 315f)   return "S";
        return "E";
    }

    private static string MaskToDirs(byte mask)
    {
        if (mask == 0) return "none";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(DirNames[i]);
            }
        }
        return sb.ToString();
    }
}
