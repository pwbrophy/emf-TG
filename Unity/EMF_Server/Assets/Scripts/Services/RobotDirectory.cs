// RobotDirectory.cs
// Keeps track of all robots, their names, IPs and assigned players.
// Also saves / loads robot names and preferred players using a simple JSON file.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class RobotDirectory : IRobotDirectory
{
    // Dictionary mapping robotId (ESP32 code) to RobotInfo.
    private readonly Dictionary<string, RobotInfo> _byId = new Dictionary<string, RobotInfo>();

    // List storing robotIds in insertion order so we can preserve a stable order.
    private readonly List<string> _order = new List<string>();

    // Counter used when auto-generating generic robot names.
    private int _nextGenericIndex = 1;

    // Events
    public event Action<RobotInfo> OnRobotAdded;
    public event Action<RobotInfo> OnRobotUpdated;
    public event Action<string> OnRobotRemoved;

    // ---------- Persistent save data types ----------

    [Serializable]
    private class RobotSaveRecord
    {
        public string id;
        public string name;
        public string preferredPlayer;
    }

    [Serializable]
    private class RobotSaveCollection
    {
        public List<RobotSaveRecord> robots = new List<RobotSaveRecord>();
    }

    private readonly Dictionary<string, RobotSaveRecord> _savedById = new Dictionary<string, RobotSaveRecord>();
    private bool _loadedSave = false;

    // ---------- Public API ----------

    public IReadOnlyList<RobotInfo> GetAll()
    {
        List<RobotInfo> result = new List<RobotInfo>(_order.Count);
        foreach (string id in _order)
        {
            if (_byId.TryGetValue(id, out var r))
                result.Add(r);
        }
        return result;
    }

    public bool TryGet(string robotId, out RobotInfo info)
    {
        return _byId.TryGetValue(robotId, out info);
    }

    public void Upsert(string robotId, string callsign, string ip)
    {
        if (string.IsNullOrWhiteSpace(robotId)) return;

        EnsureLoadedSave();

        _savedById.TryGetValue(robotId, out var savedRecord);
        bool hasSavedName =
            savedRecord != null && !string.IsNullOrWhiteSpace(savedRecord.name);
        bool hasSavedPreferredPlayer =
            savedRecord != null && !string.IsNullOrWhiteSpace(savedRecord.preferredPlayer);

        if (!_byId.TryGetValue(robotId, out var r))
        {
            string initialName;

            if (hasSavedName)
            {
                initialName = savedRecord.name;
                Debug.Log("[RobotDirectory] Loaded saved name '" + initialName + "' for robot " + robotId);
            }
            else if (!string.IsNullOrWhiteSpace(callsign))
            {
                initialName = callsign.Trim();
            }
            else
            {
                initialName = GenerateGenericName();
            }

            r = new RobotInfo
            {
                RobotId = robotId,
                Callsign = initialName,
                Ip = string.IsNullOrWhiteSpace(ip) ? "" : ip.Trim(),
                AssignedPlayer = null
            };

            string preferredForPick = hasSavedPreferredPlayer ? savedRecord.preferredPlayer : null;
            string assigned = PickAssignedPlayer(preferredForPick);

            if (!string.IsNullOrWhiteSpace(assigned))
            {
                r.AssignedPlayer = assigned;
                if (hasSavedPreferredPlayer)
                    Debug.Log("[RobotDirectory] Loaded preferred player '" + assigned + "' for robot " + robotId);
                else
                    Debug.Log("[RobotDirectory] Assigned default player '" + assigned + "' to new robot " + robotId);
            }

            Debug.Log("[RobotDirectory] Robot added: " + robotId +
                      " callsign='" + r.Callsign + "' ip='" + r.Ip + "'");
            _byId.Add(robotId, r);
            _order.Add(robotId);
            OnRobotAdded?.Invoke(r);
        }
        else
        {
            bool changed = false;

            if (hasSavedName)
            {
                if (r.Callsign != savedRecord.name)
                {
                    r.Callsign = savedRecord.name;
                    changed = true;
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(callsign))
                {
                    string newName = callsign.Trim();
                    if (r.Callsign != newName)
                    {
                        r.Callsign = newName;
                        changed = true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                string newIp = ip.Trim();
                if (r.Ip != newIp)
                {
                    r.Ip = newIp;
                    changed = true;
                }
            }

            if (string.IsNullOrWhiteSpace(r.AssignedPlayer))
            {
                string preferredForPick = hasSavedPreferredPlayer ? savedRecord.preferredPlayer : null;
                string assigned = PickAssignedPlayer(preferredForPick);

                if (!string.IsNullOrWhiteSpace(assigned))
                {
                    r.AssignedPlayer = assigned;
                    changed = true;
                    Debug.Log("[RobotDirectory] Auto-assigned player '" + assigned + "' to existing robot " + robotId);
                }
            }

            if (changed)
                OnRobotUpdated?.Invoke(r);
        }
    }

    public void SetCallsign(string robotId, string callsign)
    {
        if (!_byId.TryGetValue(robotId, out var r)) return;
        if (string.IsNullOrWhiteSpace(callsign)) return;

        string newName = callsign.Trim();
        if (r.Callsign == newName) return;

        r.Callsign = newName;
        OnRobotUpdated?.Invoke(r);

        EnsureLoadedSave();

        if (!_savedById.TryGetValue(robotId, out var record))
        {
            record = new RobotSaveRecord();
            record.id = robotId;
            _savedById[robotId] = record;
        }

        record.name = newName;
        Debug.Log("[RobotDirectory] Saved name '" + newName + "' for robot " + robotId);
        SaveToDisk();
    }

    public void SetAssignedPlayer(string robotId, string playerName)
    {
        if (!_byId.TryGetValue(robotId, out var r)) return;

        string chosen = PickAssignedPlayer(playerName);

        if (string.IsNullOrWhiteSpace(chosen))
            return;

        if (r.AssignedPlayer == chosen)
            return;

        r.AssignedPlayer = chosen;
        OnRobotUpdated?.Invoke(r);

        EnsureLoadedSave();

        if (!_savedById.TryGetValue(robotId, out var record))
        {
            record = new RobotSaveRecord();
            record.id = robotId;
            _savedById[robotId] = record;
        }

        record.preferredPlayer = chosen;
        Debug.Log("[RobotDirectory] Saved preferred player '" + chosen + "' for robot " + robotId);
        SaveToDisk();
    }

    public void ClearAssignedPlayer(string robotId)
    {
        if (!_byId.TryGetValue(robotId, out var r)) return;
        if (string.IsNullOrEmpty(r.AssignedPlayer)) return;
        r.AssignedPlayer = null;
        OnRobotUpdated?.Invoke(r);
    }

    public bool Remove(string robotId)
    {
        if (!_byId.ContainsKey(robotId))
            return false;

        _byId.Remove(robotId);
        _order.Remove(robotId);
        OnRobotRemoved?.Invoke(robotId);
        return true;
    }

    public bool RemoveLast()
    {
        if (_order.Count == 0)
            return false;

        string lastId = _order[_order.Count - 1];
        return Remove(lastId);
    }

    // ---------- Helper methods ----------

    private string GenerateGenericName()
    {
        string name = "Robot" + _nextGenericIndex.ToString("00");
        _nextGenericIndex++;
        return name;
    }

    private string PickAssignedPlayer(string preferredPlayerName)
    {
        var playersService = ServiceLocator.Players;
        if (playersService == null) return null;

        var players = playersService.GetAll();
        if (players == null || players.Count == 0) return null;

        // Build the set of players who already have a robot so we never double-assign.
        var taken = new System.Collections.Generic.HashSet<string>();
        foreach (var r in _byId.Values)
            if (!string.IsNullOrEmpty(r.AssignedPlayer))
                taken.Add(r.AssignedPlayer);

        // Honour the preferred player first if they are free.
        if (!string.IsNullOrWhiteSpace(preferredPlayerName))
        {
            for (int i = 0; i < players.Count; i++)
                if (players[i].Name == preferredPlayerName && !taken.Contains(players[i].Name))
                    return players[i].Name;
        }

        // Fall back to the first player who has no robot yet.
        for (int i = 0; i < players.Count; i++)
            if (!taken.Contains(players[i].Name))
                return players[i].Name;

        return null; // every player already has a robot
    }

    private void EnsureLoadedSave()
    {
        if (_loadedSave) return;
        LoadFromDisk();
        _loadedSave = true;
    }

    private string GetSavePath()
    {
        return Path.Combine(Application.persistentDataPath, "robots.json");
    }

    private void LoadFromDisk()
    {
        string path = GetSavePath();

        if (!File.Exists(path))
        {
            Debug.Log("[RobotDirectory] No existing robots.json file to load.");
            return;
        }

        try
        {
            string json = File.ReadAllText(path);
            var collection = JsonUtility.FromJson<RobotSaveCollection>(json);

            _savedById.Clear();

            if (collection != null && collection.robots != null)
            {
                foreach (var rec in collection.robots)
                {
                    if (string.IsNullOrWhiteSpace(rec.id))
                        continue;

                    _savedById[rec.id] = rec;
                }
            }

            Debug.Log("[RobotDirectory] Loaded robot settings for " + _savedById.Count + " robots from " + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RobotDirectory] LoadFromDisk failed: " + e.Message);
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var collection = new RobotSaveCollection();
            collection.robots = new List<RobotSaveRecord>();

            foreach (var kvp in _savedById)
                collection.robots.Add(kvp.Value);

            string json = JsonUtility.ToJson(collection, true);
            string path = GetSavePath();
            File.WriteAllText(path, json);

            Debug.Log("[RobotDirectory] Saved robot settings for " + _savedById.Count + " robots to " + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RobotDirectory] SaveToDisk failed: " + e.Message);
        }
    }
}
