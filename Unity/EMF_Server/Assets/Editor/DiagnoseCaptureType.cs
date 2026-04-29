using UnityEditor;
using UnityEngine;

public static class DiagnoseCaptureType
{
    [MenuItem("Thundergeddon/Diagnose CapturePointsPanelUI")]
    public static void Execute()
    {
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var t in asm.GetTypes())
            {
                if (t.Name.Contains("Capture"))
                    Debug.Log($"Found: {t.FullName} in {asm.GetName().Name}");
            }
        }

        // Also try TypeCache
        var types = UnityEditor.TypeCache.GetTypesDerivedFrom<MonoBehaviour>();
        foreach (var t in types)
            if (t.Name.Contains("Capture"))
                Debug.Log($"TypeCache: {t.FullName}");
    }
}
