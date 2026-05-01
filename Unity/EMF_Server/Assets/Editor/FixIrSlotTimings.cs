using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class FixIrSlotTimings
{
    public static void Execute()
    {
        var schedulers = Object.FindObjectsOfType<IrSlotScheduler>();
        if (schedulers.Length == 0)
        {
            Debug.LogError("[FixIrSlotTimings] No IrSlotScheduler found in scene!");
            return;
        }
        foreach (var s in schedulers)
        {
            var so = new SerializedObject(s);
            so.FindProperty("slotFutureMs").intValue      = 200;
            so.FindProperty("b1DurMs").intValue            = 25;
            so.FindProperty("gap12Ms").intValue            = 20;
            so.FindProperty("b2DurMs").intValue            = 25;
            so.FindProperty("repGapMs").intValue           = 20;
            so.FindProperty("reps").intValue               = 3;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(s);
            Debug.Log($"[FixIrSlotTimings] Updated IrSlotScheduler on {s.gameObject.name}: " +
                      "slotFutureMs=200 b1=25ms gap=20ms b2=25ms repGap=20ms reps=3");
        }
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[FixIrSlotTimings] Scene saved.");
    }
}
