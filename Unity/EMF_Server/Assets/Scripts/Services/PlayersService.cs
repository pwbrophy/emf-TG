// PlayersService.cs
// Stores the list of players, raises OnChanged when they change,
// and also saves / loads them to a JSON file in Application.persistentDataPath.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class PlayersService
{
    // This list holds our current players in memory.
    private readonly List<PlayerInfo> _players = new List<PlayerInfo>();

    // Raised whenever players are added/removed/renamed or alliances change.
    public event Action OnChanged;

    // Small serializable DTO for saving a single player to JSON.
    [Serializable]
    private class PlayerData
    {
        public string name;
        public int allianceIndex;
    }

    // Wrapper for a list of players, because JsonUtility likes a single root object.
    [Serializable]
    private class PlayersSaveData
    {
        public List<PlayerData> players = new List<PlayerData>();
    }

    // Returns the full list of players as a read-only view.
    public IReadOnlyList<PlayerInfo> GetAll()
    {
        return _players;
    }

    // Computes the path of our save file in a cross-platform way.
    private string GetSavePath()
    {
        return Path.Combine(Application.persistentDataPath, "players.json");
    }

    // Players are populated dynamically as phones join — no pre-seeded defaults.
    public void LoadOrEnsureDefaults() { }

    // Kept for call-site compatibility; no longer seeds Player1/Player2.
    public void EnsureDefaults() { }

    // Add a new player with a name and alliance.
    public void AddPlayer(string name, int allianceIndex)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Player" + (_players.Count + 1);
        }

        var p = new PlayerInfo
        {
            Name = name,
            AllianceIndex = allianceIndex
        };

        _players.Add(p);
        OnChanged?.Invoke();
        SaveToDisk();
    }

    // Remove a player at a specific index.
    public void RemovePlayerAt(int index)
    {
        if (index < 0 || index >= _players.Count)
            return;

        _players.RemoveAt(index);
        OnChanged?.Invoke();
        SaveToDisk();
    }

    // Rename an existing player.
    public void RenamePlayer(int index, string newName)
    {
        if (index < 0 || index >= _players.Count)
            return;

        if (string.IsNullOrWhiteSpace(newName))
            return;

        _players[index].Name = newName;
        OnChanged?.Invoke();
        SaveToDisk();
    }

    // Change which alliance a player belongs to.
    public void SetPlayerAlliance(int index, int allianceIndex, int maxAlliances)
    {
        if (index < 0 || index >= _players.Count)
            return;

        if (maxAlliances <= 0) maxAlliances = 1;
        if (allianceIndex < 0) allianceIndex = 0;
        if (allianceIndex >= maxAlliances) allianceIndex = maxAlliances - 1;

        _players[index].AllianceIndex = allianceIndex;
        OnChanged?.Invoke();
        SaveToDisk();
    }

    // Try to load players from the JSON file.
    private bool LoadFromDisk()
    {
        string path = GetSavePath();

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<PlayersSaveData>(json);

            _players.Clear();

            if (data != null && data.players != null)
            {
                foreach (var pd in data.players)
                {
                    var p = new PlayerInfo
                    {
                        Name = pd.name,
                        AllianceIndex = pd.allianceIndex
                    };
                    _players.Add(p);
                }
            }

            bool hasAny = _players.Count > 0;
            if (hasAny) OnChanged?.Invoke();
            return hasAny;
        }
        catch (Exception e)
        {
            Debug.LogWarning("PlayersService.LoadFromDisk failed: " + e.Message);
            return false;
        }
    }

    // Save current players list to the JSON file.
    private void SaveToDisk()
    {
        try
        {
            var data = new PlayersSaveData();
            data.players = new List<PlayerData>();

            foreach (var p in _players)
            {
                var pd = new PlayerData
                {
                    name = p.Name,
                    allianceIndex = p.AllianceIndex
                };
                data.players.Add(pd);
            }

            string json = JsonUtility.ToJson(data, true);
            string path = GetSavePath();
            File.WriteAllText(path, json);
        }
        catch (Exception e)
        {
            Debug.LogWarning("PlayersService.SaveToDisk failed: " + e.Message);
        }
    }
}
