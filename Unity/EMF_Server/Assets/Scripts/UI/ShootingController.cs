using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ShootingController - realtime shooting with 3-second per-robot cooldown.
public class ShootingController : MonoBehaviour
{
    [Header("UI wiring")]
    [SerializeField] private Button shootButton;
    [SerializeField] private TextMeshProUGUI resultLabel;
    [SerializeField] private TextMeshProUGUI cooldownLabel;
    [SerializeField] private RobotSelectionPanel selectionPanel;

    [Header("Timings")]
    [SerializeField] private float fireCooldownSeconds = 3f;
    [SerializeField] private float listenMsPerTank = 150f;
    [SerializeField] private float prepareTimeoutSeconds = 1.0f;
    [SerializeField] private float resultTimeoutSeconds  = 1.0f;

    // Per-robot cooldown: robotId -> time when next shot is allowed
    private readonly Dictionary<string, float> _nextFireTime = new Dictionary<string, float>();

    private bool _shootInProgress = false;

    private void Awake()
    {
        ServiceLocator.Shooting = this;
    }

    private void OnEnable()
    {
        if (shootButton != null)
            shootButton.onClick.AddListener(OnShootClicked);
    }

    private void OnDisable()
    {
        if (shootButton != null)
            shootButton.onClick.RemoveListener(OnShootClicked);
    }

    private void Update()
    {
        var robotId = selectionPanel?.CurrentRobotId;
        if (string.IsNullOrEmpty(robotId))
        {
            if (shootButton) shootButton.interactable = false;
            if (cooldownLabel) cooldownLabel.text = "";
            return;
        }

        float remaining = CooldownRemaining(robotId);
        bool ready = remaining <= 0f && !_shootInProgress;

        if (shootButton) shootButton.interactable = ready;

        if (cooldownLabel)
            cooldownLabel.text = remaining > 0f ? $"Cooldown: {remaining:F1}s" : "";
    }

    /// <summary>
    /// Called by PlayerWebSocketServer to fire from a phone player's request.
    /// </summary>
    public void RequestFire(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return;
        if (_shootInProgress) return;
        if (CooldownRemaining(robotId) > 0f) return;

        Debug.Log("[Shooting] RequestFire from phone: " + robotId);
        _nextFireTime[robotId] = Time.time + fireCooldownSeconds;
        StartCoroutine(ShootSequence(robotId));
    }

    public float CooldownRemaining(string robotId)
    {
        if (_nextFireTime.TryGetValue(robotId, out float t))
            return Mathf.Max(0f, t - Time.time);
        return 0f;
    }

    private void OnShootClicked()
    {
        if (_shootInProgress) return;

        if (selectionPanel == null)
        {
            Debug.LogWarning("[Shooting] No RobotSelectionPanel wired.");
            return;
        }

        string shooterId = selectionPanel.CurrentRobotId;
        if (string.IsNullOrEmpty(shooterId))
        {
            Debug.Log("[Shooting] No robot selected.");
            return;
        }

        if (CooldownRemaining(shooterId) > 0f)
        {
            Debug.Log("[Shooting] Still on cooldown.");
            return;
        }

        Debug.Log("[Shooting] Fire! Shooter=" + shooterId);
        _nextFireTime[shooterId] = Time.time + fireCooldownSeconds;
        StartCoroutine(ShootSequence(shooterId));
    }

    private IEnumerator ShootSequence(string shooterId)
    {
        _shootInProgress = true;

        var server  = ServiceLocator.RobotServer;
        var dir     = ServiceLocator.RobotDirectory;
        var players = ServiceLocator.Players;
        var game    = ServiceLocator.Game;

        if (server == null || dir == null || players == null)
        {
            Debug.LogWarning("[Shooting] Missing required services.");
            _shootInProgress = false;
            yield break;
        }

        if (resultLabel != null) resultLabel.text = "";

        // Build alliance lookup
        var playerList = players.GetAll();
        int GetAllianceIndex(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return -1;
            for (int i = 0; i < playerList.Count; i++)
                if (playerList[i].Name == playerName) return playerList[i].AllianceIndex;
            return -1;
        }

        if (!dir.TryGet(shooterId, out var shooterInfo))
        {
            Debug.LogWarning("[Shooting] Shooter not in directory: " + shooterId);
            _shootInProgress = false;
            yield break;
        }

        int shooterAlliance = GetAllianceIndex(shooterInfo.AssignedPlayer);
        if (shooterAlliance < 0)
        {
            Debug.LogWarning("[Shooting] Shooter has no valid alliance.");
            _shootInProgress = false;
            yield break;
        }

        // Build enemy list (different alliance, has a player, not dead)
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
            enemies.Add(r);
        }

        // Always give the shooter visual/audio feedback immediately.
        server.SendFlashFire(shooterId);

        if (enemies.Count == 0)
        {
            if (resultLabel != null) resultLabel.text = "No enemies.";
            _shootInProgress = false;
            yield break;
        }

        server.SendDrive(shooterId, 0f, 0f);
        server.SendTurret(shooterId, 0f);

        var hitResults = new List<(RobotInfo robot, string direction)>();
        int listenMs   = Mathf.Max(1, Mathf.RoundToInt(listenMsPerTank));

        foreach (var enemy in enemies)
        {
            string enemyId = enemy.RobotId;

            // Step A: ask enemy to emit IR
            bool emitReady = false;
            void ReadyHandler(string rid) { if (rid == enemyId) emitReady = true; }
            server.OnIrEmitReady += ReadyHandler;
            server.SendIrEmitPrepare(enemyId);

            float t0 = Time.time;
            while (!emitReady && Time.time - t0 < prepareTimeoutSeconds) yield return null;
            server.OnIrEmitReady -= ReadyHandler;

            if (!emitReady)
            {
                Debug.LogWarning("[Shooting] ir_emit_ready timeout for " + enemyId);
                server.SendIrEmitStop(enemyId);
                continue;
            }

            // Step B: shooter listens
            bool   resultReceived = false;
            bool   hit            = false;
            string hitDir         = "";

            void ResultHandler(string rid, bool wasHit, string direction)
            {
                if (rid == shooterId)
                {
                    hit            = wasHit;
                    hitDir         = direction;
                    resultReceived = true;
                }
            }

            server.OnIrResult += ResultHandler;
            server.SendIrListenAndReport(shooterId, listenMs);

            float t1 = Time.time;
            while (!resultReceived && Time.time - t1 < resultTimeoutSeconds) yield return null;
            server.OnIrResult -= ResultHandler;

            // Step C: stop enemy emit
            server.SendIrEmitStop(enemyId);

            if (!resultReceived) { Debug.LogWarning("[Shooting] ir_result timeout"); continue; }

            if (hit)
            {
                Debug.Log($"[Shooting] HIT: {enemy.Callsign} dir={hitDir}");
                hitResults.Add((enemy, hitDir));
            }
        }

        // Apply damage for all confirmed hits
        var settings = ServiceLocator.GameSettings;
        int maxHp    = settings != null ? settings.MaxHp : 100;

        foreach (var (enemy, hitDirection) in hitResults)
        {
            // Send visual/audio feedback to the hit robot
            server.SendFlashHit(enemy.RobotId);

            // Apply damage (game service handles HP update, death, win condition)
            int damage = 0;
            if (game != null)
                damage = game.ApplyDamage(shooterId, enemy.RobotId, hitDirection, players, dir);

            // Send updated HP to the hit robot for its LED bar
            int newHp = game?.State?.RobotHp.GetValueOrDefault(enemy.RobotId, 0) ?? 0;
            server.SendSetHp(enemy.RobotId, newHp, maxHp);
        }

        // Update result label
        if (resultLabel != null)
        {
            if (hitResults.Count == 0)
            {
                resultLabel.text = "Miss!";
            }
            else
            {
                var sb = new System.Text.StringBuilder("Hit: ");
                for (int i = 0; i < hitResults.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var r = hitResults[i].robot;
                    sb.Append(string.IsNullOrEmpty(r.Callsign) ? r.RobotId : r.Callsign);
                    if (!string.IsNullOrEmpty(hitResults[i].direction))
                        sb.Append($" ({hitResults[i].direction})");
                }
                resultLabel.text = sb.ToString();
            }
        }

        _shootInProgress = false;
    }
}
