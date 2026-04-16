using System;
using System.Collections.Generic;

// The registry the UI talks to.
// We keep only robots that are "present".
// When a robot disappears, call Remove(robotId).
public interface IRobotDirectory
{
    IReadOnlyList<RobotInfo> GetAll();

    // Events that presenters listen to for live updates
    event Action<RobotInfo> OnRobotAdded;
    event Action<RobotInfo> OnRobotUpdated;
    event Action<string> OnRobotRemoved; // robotId

    // Add or update a robot that is present.
    // If it's new -> added; if it exists -> updated fields (name/ip/player).
    void Upsert(string robotId, string callsign, string ip);

    // Change editable fields
    void SetCallsign(string robotId, string newCallsign);
    void SetAssignedPlayer(string robotId, string playerName);
    void ClearAssignedPlayer(string robotId);

    // Robot is gone (disconnected or timed out) -> remove from registry
    bool Remove(string robotId);

    // Remove the most recently added robot (by insertion order)
    bool RemoveLast();

    // Helper to query
    bool TryGet(string robotId, out RobotInfo info);
}
