// CapturePointBeaconDirectory.cs
// Tracks the 3 fixed physical capture-point beacon devices (North/Centre/South).
// Unlike RobotDirectory, the set of entries never changes — it's pre-seeded at
// construction and entries are only ever marked connected/disconnected, never
// added or removed, since there are always exactly 3 capture points.

using System;
using System.Collections.Generic;

public class BeaconInfo
{
    public int    PointIndex;
    public string PointName;
    public string BeaconId;
    public string Ip;
    public bool   Connected;
    public float  LastSeenTime;
}

public class CapturePointBeaconDirectory
{
    private static readonly string[] PointNames = { "North", "Centre", "South" };

    private readonly BeaconInfo[] _byPointIndex = new BeaconInfo[3];

    public event Action<int> OnBeaconUpdated;

    public CapturePointBeaconDirectory()
    {
        for (int i = 0; i < _byPointIndex.Length; i++)
        {
            _byPointIndex[i] = new BeaconInfo
            {
                PointIndex = i,
                PointName  = PointNames[i],
                BeaconId   = "",
                Ip         = "",
                Connected  = false
            };
        }
    }

    public IReadOnlyList<BeaconInfo> GetAll() => _byPointIndex;

    public bool TryGet(int pointIndex, out BeaconInfo info)
    {
        if (pointIndex < 0 || pointIndex >= _byPointIndex.Length)
        {
            info = null;
            return false;
        }
        info = _byPointIndex[pointIndex];
        return true;
    }

    // Called when a beacon's hello/heartbeat is received.
    public void Upsert(int pointIndex, string beaconId, string ip, float now)
    {
        if (pointIndex < 0 || pointIndex >= _byPointIndex.Length) return;

        var b = _byPointIndex[pointIndex];
        b.LastSeenTime = now;

        bool changed = !b.Connected;
        b.Connected = true;

        if (!string.IsNullOrWhiteSpace(beaconId) && b.BeaconId != beaconId)
        {
            b.BeaconId = beaconId;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(ip) && b.Ip != ip)
        {
            b.Ip = ip;
            changed = true;
        }

        if (changed) OnBeaconUpdated?.Invoke(pointIndex);
    }

    // Called only by the heartbeat-timeout sweep — never on socket close.
    public void MarkDisconnected(int pointIndex)
    {
        if (pointIndex < 0 || pointIndex >= _byPointIndex.Length) return;
        var b = _byPointIndex[pointIndex];
        if (!b.Connected) return;
        b.Connected = false;
        OnBeaconUpdated?.Invoke(pointIndex);
    }
}
