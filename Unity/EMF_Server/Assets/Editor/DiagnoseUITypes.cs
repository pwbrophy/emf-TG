using UnityEditor;
using UnityEngine;

public static class DiagnoseUITypes
{
    [MenuItem("Thundergeddon/Diagnose UI Types")]
    public static void Execute()
    {
        string[] names = { "TeamRosterPanel", "TeamPointsBarUI", "EventLogPanelUI",
                           "CapturePointsPanelUI", "PauseGameButton" };

        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var n in names)
            {
                var t = asm.GetType(n);
                if (t != null)
                    Debug.Log($"[UITypes] FOUND {n} in {asm.GetName().Name}");
            }
        }

        // Check TypeCache too
        foreach (var t in UnityEditor.TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
            foreach (var n in names)
                if (t.Name == n)
                    Debug.Log($"[UITypes] TypeCache FOUND {n} in {t.Assembly.GetName().Name}");

        Debug.Log("[UITypes] Search complete.");
    }
}
