// Minimal robot data stored by the registry.
public class RobotInfo
{
    public string RobotId;         // stable unique id (e.g., MAC-based) - the "key"
    public string Callsign;        // nice display name (editable)
    public string Ip;              // last-known local IP
    public string AssignedPlayer;  // "Player1" or "Player2" for now
    public bool   HFlip;           // camera horizontal mirror (reported by robot in hello)
    public bool   VFlip;           // camera vertical flip (reported by robot in hello)
    public bool   InvThrottle;     // drive: forward/backward inverted (saved to robot NVS)
    public bool   InvSteer;        // drive: left/right inverted (saved to robot NVS)
    public bool   InvTurret;       // turret: rotation direction inverted (saved to robot NVS)
    public int    PreferredAlliance = -1; // 0=Desert Squad, 1=Jungle Squad, -1=unset
}
