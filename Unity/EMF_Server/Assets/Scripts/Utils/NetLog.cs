// NetLog.cs - switch for high-volume per-message logging (robot commands, IR shot
// steps, pings). Off by default: in the Editor every Debug.Log is kept in the
// Console until cleared, and a busy match logs dozens of lines per shot - around a
// million entries over an event day, which slows the Editor down as it runs.
// Failures and warnings are still logged unconditionally at their call sites.
public static class NetLog
{
    public static bool Verbose = false;

    public static void Log(string msg)
    {
        if (Verbose) UnityEngine.Debug.Log(msg);
    }
}
