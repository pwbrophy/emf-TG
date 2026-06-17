using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;

public class WirePlayingSettingsPanel
{
    [MenuItem("Thundergeddon/Wire Playing Settings Panel")]
    public static void Execute()
    {
        var canvas     = GameObject.Find("Canvas");
        var playingT   = canvas?.transform.Find("PlayingPanel");
        if (playingT == null) { Debug.LogError("PlayingPanel not found"); return; }

        // Remove existing if present
        var existing = playingT.Find("LiveSettingsPanel");
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        // Create the panel container
        var panel = new GameObject("LiveSettingsPanel");
        var rt    = panel.AddComponent<RectTransform>();
        panel.transform.SetParent(playingT, false);

        // Place it in the bottom-left corner of PlayingPanel
        // (below any robot info, alongside the HP panel)
        rt.anchorMin = new Vector2(0f,   0f);
        rt.anchorMax = new Vector2(0.3f, 0.55f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        // Dark background
        var img = panel.AddComponent<Image>();
        img.color = new Color(0.07f, 0.07f, 0.11f, 0.92f);

        // Add the component — it self-builds rows at Awake/Start
        panel.AddComponent<PlayingSettingsPanel>();

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[Wire] LiveSettingsPanel added to PlayingPanel and scene saved.");
    }
}
