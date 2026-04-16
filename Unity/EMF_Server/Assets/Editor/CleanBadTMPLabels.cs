using UnityEngine;
using UnityEditor;
using TMPro;

/// <summary>
/// Removes any TextMeshProUGUI components where m_canvasRenderer is null
/// (created with multi-type new GameObject constructor), so LayoutPanels
/// can recreate them cleanly.
/// </summary>
public static class CleanBadTMPLabels
{
    public static void Execute()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        int removed = 0;

        foreach (var root in roots)
        {
            foreach (var tmp in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                // canvasRenderer property throws if m_canvasRenderer is null
                CanvasRenderer cr = null;
                try { cr = tmp.canvasRenderer; } catch { }

                if (cr == null)
                {
                    string path = GetPath(tmp.gameObject);
                    // Destroy the whole label GO if it was auto-created (has no siblings we care about)
                    // Only remove the ones we created as pure label GOs (no other components except RT + TMP)
                    var comps = tmp.gameObject.GetComponents<Component>();
                    // Pure label: Transform, TextMeshProUGUI (and optionally CanvasRenderer)
                    bool isPureLabel = true;
                    foreach (var c in comps)
                    {
                        if (c is Transform || c is TextMeshProUGUI || c is CanvasRenderer) continue;
                        isPureLabel = false; break;
                    }
                    if (isPureLabel)
                    {
                        Debug.Log($"[CleanBadTMPLabels] Removing bad label: {path}");
                        Object.DestroyImmediate(tmp.gameObject);
                        removed++;
                    }
                }
            }
        }

        EditorUtility.SetDirty(scene.GetRootGameObjects()[0]);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log($"[CleanBadTMPLabels] Removed {removed} bad labels.");
    }

    static string GetPath(GameObject go)
    {
        string path = go.name;
        var t = go.transform.parent;
        while (t != null) { path = t.name + "/" + path; t = t.parent; }
        return path;
    }
}
