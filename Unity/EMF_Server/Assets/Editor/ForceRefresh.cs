using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

public static class ForceRefresh
{
    [MenuItem("Thundergeddon/Force Asset Refresh")]
    public static void Execute()
    {
        // Defer so this call returns before the recompile blocks the editor
        EditorApplication.delayCall += () =>
        {
            AssetDatabase.Refresh();
            Debug.Log("[ForceRefresh] AssetDatabase.Refresh() called");
        };
        Debug.Log("[ForceRefresh] Deferred refresh queued");
    }
}
