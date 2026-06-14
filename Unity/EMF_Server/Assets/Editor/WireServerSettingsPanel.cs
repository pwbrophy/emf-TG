using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Moves the 2-player mode toggle from the lobby's GameSettingsPanel to a new
/// ServerSettingsPanel on MainMenuPanel. Server settings are configured before
/// entering the lobby so they can't be changed mid-session.
///
/// Menu: Thundergeddon → Wire Server Settings Panel (Main Menu)
/// Safe to re-run — skips steps that are already done.
/// </summary>
public static class WireServerSettingsPanel
{
    [MenuItem("Thundergeddon/Wire Server Settings Panel (Main Menu)")]
    public static void Execute()
    {
        var scene  = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var roots  = scene.GetRootGameObjects();

        GameObject canvas = null;
        foreach (var r in roots) if (r.name == "Canvas") { canvas = r; break; }
        if (canvas == null) { Debug.LogError("[WireSSP] Canvas not found."); return; }

        var mainMenu = canvas.transform.Find("MainMenuPanel")?.gameObject;
        if (mainMenu == null) { Debug.LogError("[WireSSP] MainMenuPanel not found."); return; }

        // ── 1. Remove TwoPlayerRow from GameSettingsPanel (lobby) ────────────
        RemoveLobbyTwoPlayerRow(canvas);

        // ── 2. Add ServerSettingsPanel to MainMenuPanel ──────────────────────
        AddServerSettingsPanel(mainMenu);

        EditorUtility.SetDirty(canvas);
        if (!Application.isPlaying)
        {
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            Debug.Log("[WireSSP] Done — ServerSettingsPanel wired on MainMenuPanel, scene saved.");
        }
        else
        {
            Debug.Log("[WireSSP] Done — ServerSettingsPanel wired. Exit Play mode and re-run to save the scene.");
        }
    }

    static void RemoveLobbyTwoPlayerRow(GameObject canvas)
    {
        var gspGO = canvas.transform.Find("LobbyPanel/GameSettingsPanel");
        if (gspGO == null) { Debug.LogWarning("[WireSSP] GameSettingsPanel not found in lobby — skipping row removal."); return; }

        var row = gspGO.Find("TwoPlayerRow");
        if (row != null)
        {
            GameObject.DestroyImmediate(row.gameObject);
            Debug.Log("[WireSSP] Removed TwoPlayerRow from GameSettingsPanel.");
        }
        else
        {
            Debug.Log("[WireSSP] TwoPlayerRow not in GameSettingsPanel — nothing to remove.");
        }
    }

    static void AddServerSettingsPanel(GameObject mainMenu)
    {
        // Add or find container
        var existing = mainMenu.transform.Find("ServerSettingsPanel");
        GameObject container;
        if (existing != null)
        {
            container = existing.gameObject;
            Debug.Log("[WireSSP] ServerSettingsPanel already exists on MainMenuPanel.");
        }
        else
        {
            container = new GameObject("ServerSettingsPanel");
            var rt = container.AddComponent<RectTransform>();
            container.transform.SetParent(mainMenu.transform, false);

            // Position in bottom portion of MainMenuPanel
            rt.anchorMin        = new Vector2(0.1f, 0.02f);
            rt.anchorMax        = new Vector2(0.9f, 0.22f);
            rt.offsetMin        = Vector2.zero;
            rt.offsetMax        = Vector2.zero;

            var vlg = container.AddComponent<VerticalLayoutGroup>();
            vlg.spacing                = 6f;
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlHeight     = true;
            vlg.padding                = new RectOffset(8, 8, 6, 6);

            // Background image
            var img = container.AddComponent<Image>();
            img.color = new Color(0.08f, 0.08f, 0.16f, 0.85f);
        }

        // Header label
        if (container.transform.Find("Header") == null)
        {
            var hdrGO = new GameObject("Header");
            hdrGO.AddComponent<RectTransform>();
            hdrGO.transform.SetParent(container.transform, false);
            var tmp = hdrGO.AddComponent<TextMeshProUGUI>();
            tmp.text      = "SERVER SETTINGS";
            tmp.fontSize  = 11f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color     = new Color(0.5f, 0.5f, 0.7f, 1f);
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.characterSpacing = 4f;
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if (font != null) tmp.font = font;
            hdrGO.AddComponent<LayoutElement>().preferredHeight = 18f;
        }

        // 2-player toggle row
        Toggle toggle = null;
        var existingRow = container.transform.Find("TwoPlayerRow");
        if (existingRow != null)
        {
            toggle = existingRow.GetComponentInChildren<Toggle>(true);
            Debug.Log("[WireSSP] TwoPlayerRow already exists on ServerSettingsPanel.");
        }
        else
        {
            var row = new GameObject("TwoPlayerRow");
            row.AddComponent<RectTransform>();
            row.transform.SetParent(container.transform, false);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing                = 8f;
            hlg.childForceExpandWidth  = false;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = hlg.childControlHeight = true;
            row.AddComponent<LayoutElement>().preferredHeight = 28f;

            // Toggle box
            var cbGO = new GameObject("Toggle");
            cbGO.AddComponent<RectTransform>();
            cbGO.transform.SetParent(row.transform, false);
            var cbLE = cbGO.AddComponent<LayoutElement>();
            cbLE.preferredWidth = cbLE.preferredHeight = 22f;
            cbLE.flexibleWidth  = 0f;
            var bgImg = cbGO.AddComponent<Image>();
            bgImg.color = new Color(0.14f, 0.14f, 0.14f);
            toggle = cbGO.AddComponent<Toggle>();
            toggle.targetGraphic = bgImg;

            var checkGO = new GameObject("Checkmark");
            var checkRT = checkGO.AddComponent<RectTransform>();
            checkGO.transform.SetParent(cbGO.transform, false);
            checkRT.anchorMin = new Vector2(0.15f, 0.15f);
            checkRT.anchorMax = new Vector2(0.85f, 0.85f);
            checkRT.offsetMin = checkRT.offsetMax = Vector2.zero;
            var checkImg = checkGO.AddComponent<Image>();
            checkImg.color = new Color(1.000f, 0.306f, 0.110f);
            toggle.graphic = checkImg;
            toggle.isOn    = false;

            // Label
            var lblGO = new GameObject("Lbl");
            lblGO.AddComponent<RectTransform>();
            lblGO.transform.SetParent(row.transform, false);
            lblGO.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var lbl = lblGO.AddComponent<TextMeshProUGUI>();
            lbl.text      = "2-Player Mode (driver + gunner)";
            lbl.fontSize  = 12f;
            lbl.color     = new Color(0.85f, 0.85f, 0.85f, 1f);
            lbl.alignment = TextAlignmentOptions.MidlineLeft;
            var font2 = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if (font2 != null) lbl.font = font2;
        }

        // Add / wire ServerSettingsPanel component
        var comp = container.GetComponent<ServerSettingsPanel>();
        if (comp == null) comp = container.AddComponent<ServerSettingsPanel>();

        if (toggle != null)
        {
            var so   = new SerializedObject(comp);
            var prop = so.FindProperty("twoPlayerToggle");
            if (prop != null)
            {
                so.Update();
                prop.objectReferenceValue = toggle;
                so.ApplyModifiedProperties();
                Debug.Log("[WireSSP] twoPlayerToggle wired on ServerSettingsPanel.");
            }
        }

        EditorUtility.SetDirty(container);
    }
}
