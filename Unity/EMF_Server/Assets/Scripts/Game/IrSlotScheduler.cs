using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Manages the IR shot queue.
// One shot executes at a time (_busy gate). Uses the ACK-driven handshake
// protocol: server waits for explicit confirmations at each step, so timing
// adapts to actual Wi-Fi latency. No clock sync required.
public class IrSlotScheduler : MonoBehaviour
{
    private readonly Queue<string> _queue = new Queue<string>();
    private bool _busy;
    private int  _nextSlotId = 1;

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
        Debug.Log($"[IrHs] >>> FIRE REQUEST queued for {shooterId} (queue depth: {_queue.Count})");
    }

    private void Update()
    {
        if (_busy || _queue.Count == 0) return;
        StartCoroutine(ExecuteShot(_queue.Dequeue()));
    }

    // ACK-driven handshake shot.
    //
    // Sequence:
    //   1. ir_emit_left  → wait ir_emit_ack from shooter
    //   2. ir_listen_window → wait ir_window_result from all enemies  (b1 masks)
    //   3. Early exit if all b1 == 0
    //   4. ir_emit_right + ir_listen_window sent simultaneously (no ACK wait)
    //   5. Wait ir_window_result from b1-hit enemies only  (b2 masks)
    //   6. ir_emit_stop to shooter
    //   7. Resolve hits: (b1 & b2) != 0
    private IEnumerator ExecuteShot(string shooterId)
    {
        _busy = true;
        int slotId = _nextSlotId++;

        Debug.Log($"[IrHs] ===== SHOT {slotId} START — shooter={shooterId} =====");

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
        int windowMs        = settings != null ? settings.HandshakeWindowMs        : 10;
        int windowTimeoutMs = settings != null ? settings.HandshakeWindowTimeoutMs : 300;

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
        }

        // Build b2Enemies: only enemies that detected the left LED.
        var b2Enemies = new List<RobotInfo>();
        if (!aborted)
        {
            for (int i = 0; i < enemies.Count; i++)
            {
                b1Masks.TryGetValue(enemies[i].RobotId, out byte b1);
                if (b1 != 0) b2Enemies.Add(enemies[i]);
            }
            if (b2Enemies.Count == 0)
            {
                Debug.Log($"[IrHs] Shot {slotId} — all b1 == 0, early exit (miss)");
                server.SendIrEmitStop(shooterId);
                aborted = true;
            }
        }

        // ── Phase 2: right LED ────────────────────────────────────────────────
        // Send ir_emit_right and listen windows simultaneously — no ACK wait.
        if (!aborted)
        {
            collectingB2 = true;
            server.SendIrEmitRight(shooterId);
            for (int i = 0; i < b2Enemies.Count; i++)
                server.SendIrListenWindow(b2Enemies[i].RobotId, windowMs);
            Debug.Log($"[IrHs] Shot {slotId} — ir_emit_right + b2 listen_window to {b2Enemies.Count} enemy(ies)");

            float winDeadline = Time.time + (windowMs + windowTimeoutMs) / 1000f;
            while (b2Masks.Count < b2Enemies.Count && Time.time < winDeadline) yield return null;
            Debug.Log($"[IrHs] Shot {slotId} — b2 done: {b2Masks.Count}/{b2Enemies.Count} results");

            server.SendIrEmitStop(shooterId);
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
                Debug.Log($"[IrHs] *** HIT: {enemyName} cardinal={cardinalDir} {(cardinalDir == "S" ? "(REAR — 3×)" : "")} ***");
                hitCount++;

                var state = game?.State;
                if (state != null && (state.DeadRobots.Contains(enemyId) || state.RespawningRobots.Contains(enemyId)))
                {
                    Debug.Log($"[IrHs]   {enemyName} is dead/respawning — skipping");
                    continue;
                }

                int damage = game != null ? game.ApplyDamage(shooterId, enemyId, detMask, cardinalDir, players, dir) : 0;
                int newHp  = game?.State?.RobotHp.GetValueOrDefault(enemyId, 0) ?? 0;
                Debug.Log($"[IrHs]   damage={damage} newHp={newHp}/{maxHp}");

                if (damage > 0 && newHp > 0)
                    server.SendFlashHit(enemyId);

                server.SendSetHp(enemyId, newHp, maxHp);
            }
            Debug.Log($"[IrHs] ===== Shot {slotId} DONE — {hitCount} hit(s) of {enemies.Count} =====");
        }
        else
        {
            Debug.Log($"[IrHs] ===== Shot {slotId} DONE — aborted ======");
        }

        server.OnIrEmitAck      -= OnAck;
        server.OnIrWindowResult -= OnWindow;
        _busy = false;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static readonly string[] DirNames  = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    private static readonly float[]  DirAngles = { 90f, 45f, 0f, -45f, -90f, -135f, 180f, 135f };

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
        if (Mathf.Approximately(sinSum, 0f) && Mathf.Approximately(cosSum, 0f)) return "N";
        float meanDeg = Mathf.Atan2(sinSum, cosSum) * Mathf.Rad2Deg;
        if (meanDeg < 0f) meanDeg += 360f;
        if (meanDeg >= 45f && meanDeg <= 135f) return "N";
        if (meanDeg > 135f && meanDeg <= 225f) return "W";
        if (meanDeg > 225f && meanDeg < 315f)  return "S";
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
