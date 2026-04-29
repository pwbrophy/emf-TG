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
    [Header("Slot timing (ms)")]
    [SerializeField] private int slotFutureMs       = 150;
    [SerializeField] private int b1DurMs            = 10;
    [SerializeField] private int gap12Ms            = 25;
    [SerializeField] private int b2DurMs            = 10;
    [SerializeField] private int repGapMs           = 25;
    [SerializeField] private int reps               = 2;

    [Header("Result collection")]
    [SerializeField] private float resultBufferSeconds = 0.5f;

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
        StartCoroutine(ExecuteSlot(next));
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

        // ── Time sync ──────────────────────────────────────────────────────
        long syncMs = (long)(Time.time * 1000.0);
        Debug.Log($"[IrSlot] Slot {slotId} — sending time_sync ut={syncMs} to all robots.");
        server.SendTimeSyncAll(syncMs);

        yield return null; // one frame so WS sends flush

        // ── Compute slot timing ────────────────────────────────────────────
        long slotStartMs = (long)(Time.time * 1000.0) + slotFutureMs;
        int  perRepMs    = b1DurMs + gap12Ms + b2DurMs + repGapMs;
        int  slotDurMs   = reps * perRepMs - repGapMs;
        float slotEndUnityTime = (float)(slotStartMs + slotDurMs) / 1000f;

        Debug.Log($"[IrSlot] Slot {slotId} timing: " +
                  $"start={slotStartMs} ms | dur={slotDurMs} ms | " +
                  $"reps={reps} | b1={b1DurMs}ms gap={gap12Ms}ms b2={b2DurMs}ms repGap={repGapMs}ms");
        Debug.Log($"[IrSlot] Slot {slotId} windows (relative to slot_start): " +
                  $"Burst1=[0..{b1DurMs}ms] Burst2=[{b1DurMs+gap12Ms}..{b1DurMs+gap12Ms+b2DurMs}ms] " +
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

        // ── Send fire/listen commands ──────────────────────────────────────
        bool fireSent = server.SendIrFireSlot(shooterId, slotId, slotStartMs,
                                              b1DurMs, gap12Ms, b2DurMs, repGapMs, reps);
        Debug.Log($"[IrSlot] Slot {slotId} — ir_fire_slot to {shooterId}: {(fireSent ? "OK" : "FAILED")}");

        for (int i = 0; i < enemies.Count; i++)
        {
            bool listenSent = server.SendIrListenSlot(enemies[i].RobotId, slotId, slotStartMs,
                                                      b1DurMs, gap12Ms, b2DurMs, repGapMs, reps);
            Debug.Log($"[IrSlot] Slot {slotId} — ir_listen_slot to {enemies[i].Callsign ?? enemies[i].RobotId}: {(listenSent ? "OK" : "FAILED")}");
        }

        // ── Wait for slot end + result buffer ─────────────────────────────
        float waitUntil = slotEndUnityTime + resultBufferSeconds;
        float waitSecs  = waitUntil - Time.time;
        Debug.Log($"[IrSlot] Slot {slotId} — waiting {waitSecs * 1000f:F0}ms for results...");
        while (Time.time < waitUntil) yield return null;

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

            byte bothMask = (byte)(masks.b1 & masks.b2);
            Debug.Log($"[IrSlot]   {enemyName}: b1=0x{masks.b1:X2} b2=0x{masks.b2:X2} " +
                      $"both=0x{bothMask:X2} ({MaskToDirs(bothMask)}) → {(bothMask != 0 ? "HIT" : "miss")}");

            if (bothMask == 0)
            {
                if (masks.b1 != 0 || masks.b2 != 0)
                    Debug.Log($"[IrSlot]   {enemyName}: partial detection (only one burst) — no hit.");
                continue;
            }

            string hitDir = ResolveBestDirection(bothMask);
            bool isRear   = hitDir == "S" || hitDir == "SE" || hitDir == "SW";
            Debug.Log($"[IrSlot] *** HIT: {enemyName} dir={hitDir} {(isRear ? "(REAR — 3× damage!)" : "")} ***");
            hitCount++;

            // Flash red + play damage buzzer on the hit robot
            server.SendFlashHit(enemyId);

            // Apply damage (also fires OnHpChanged → PlayerWebSocketServer sends state_update to phone)
            int damage = 0;
            if (game != null)
                damage = game.ApplyDamage(shooterId, enemyId, hitDir, players, dir);

            int newHp = game?.State?.RobotHp.GetValueOrDefault(enemyId, 0) ?? 0;
            Debug.Log($"[IrSlot]   damage={damage} newHp={newHp}/{maxHp}");

            // Update HP bar LEDs on hit robot
            server.SendSetHp(enemyId, newHp, maxHp);
        }

        Debug.Log($"[IrSlot] ============ SLOT {slotId} DONE — {hitCount} hit(s) of {enemies.Count} enemy(ies) ============");

        _busy = false;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // Direction priority: S(4) > SW(5) > SE(3) > all others (ascending bit order).
    private static readonly string[] DirNames = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    private static string ResolveBestDirection(byte mask)
    {
        if ((mask & (1 << 4)) != 0) return "S";
        if ((mask & (1 << 5)) != 0) return "SW";
        if ((mask & (1 << 3)) != 0) return "SE";
        for (int i = 0; i < 8; i++)
            if ((mask & (1 << i)) != 0) return DirNames[i];
        return "";
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
