using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Adds a "Two-Player Mode" Toggle row to the Lobby's GameSettingsPanel and wires
/// the twoPlayerToggle serialized reference on GameSettingsPanel.
/// Menu: Thundergeddon → Wire Two-Player Toggle (Lobby)
/// Safe to re-run — skips if already wired.
/// </summary>
public static class WireTwoPlayerToggle
{
    [MenuItem("Thundergeddon/Wire Two-Player Toggle (Lobby)")]
    public static void Execute()
    {
        var panels = Resources.FindObjectsOfTypeAll<GameSettingsPanel>();
        if (panels.Length == 0)
        {
            Debug.LogError("[WireTwoPlayerToggle] GameSettingsPanel not found in scene.");
            return;
        }
        var panel   = panels[0];
        var panelGo = panel.gameObject;

        var so   = new SerializedObject(panel);
        var prop = so.FindProperty("twoPlayerToggle");
        if (prop == null)
        {
            Debug.LogError("[WireTwoPlayerToggle] Field 'twoPlayerToggle' not found on GameSettingsPanel — ensure the C# field is added first.");
            return;
        }
        if (prop.objectReferenceValue != null)
        {
            Debug.Log("[WireTwoPlayerToggle] twoPlayerToggle already wired — nothing to do.");
            return;
        }

        // Find or create the row
        Transform parent = panelGo.transform;
        var existingRow = parent.Find("TwoPlayerRow");
        GameObject row;
        if (existingRow != null)
        {
            row = existingRow.gameObject;
        }
        else
        {
            row = new GameObject("TwoPlayerRow");
            row.AddComponent<RectTransform>();
            row.transform.SetParent(parent, false);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childForceExpandWidth  = false;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = hlg.childControlHeight = true;
            row.AddComponent<LayoutElement>().preferredHeight = 28f;
        }

        // Toggle box
        Toggle toggle = row.GetComponentInChildren<Toggle>(true);
        if (toggle == null)
        {
            var cbGo = new GameObject("Toggle");
            cbGo.AddComponent<RectTransform>();
            cbGo.transform.SetParent(row.transform, false);
            var cbLE = cbGo.AddComponent<LayoutElement>();
            cbLE.preferredWidth = cbLE.preferredHeight = 22f;
            cbLE.flexibleWidth = 0f;
            var bgImg = cbGo.AddComponent<Image>();
            bgImg.color = new Color(0.14f, 0.14f, 0.14f);
            toggle = cbGo.AddComponent<Toggle>();
            toggle.targetGraphic = bgImg;

            var checkGo = new GameObject("Checkmark");
            var checkRT = checkGo.AddComponent<RectTransform>();
            checkGo.transform.SetParent(cbGo.transform, false);
            checkRT.anchorMin = new Vector2(0.15f, 0.15f);
            checkRT.anchorMax = new Vector2(0.85f, 0.85f);
            checkRT.offsetMin = checkRT.offsetMax = Vector2.zero;
            var checkImg = checkGo.AddComponent<Image>();
            checkImg.color = new Color(1.000f, 0.306f, 0.110f); // accent orange-red
            toggle.graphic = checkImg;
            toggle.isOn = false;
        }

        // Label
        if (row.transform.Find("Lbl") == null)
        {
            var lblGo = new GameObject("Lbl");
            lblGo.AddComponent<RectTransform>();
            lblGo.transform.SetParent(row.transform, false);
            lblGo.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var tmp = lblGo.AddComponent<TextMeshProUGUI>();
            tmp.text      = "2-Player Mode";
            tmp.fontSize  = 12f;
            tmp.color     = new Color(0.75f, 0.75f, 0.75f);
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if (font != null) tmp.font = font;
        }

        // Wire the serialized reference
        so.Update();
        prop.objectReferenceValue = toggle;
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(panel);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(panelGo.scene);

        Debug.Log("[WireTwoPlayerToggle] Done — TwoPlayerRow added and twoPlayerToggle wired to GameSettingsPanel.");
    }
}
