using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Reflection;

/// <summary>
/// Adds two base-UID input rows to the existing GameSettingsPanel in LobbyPanel.
/// Menu: Thundergeddon → Wire Base UIDs
/// Safe to re-run — skips rows that already exist.
/// </summary>
public static class WireBaseUids
{
    [MenuItem("Thundergeddon/Wire Base UIDs")]
    public static void Execute()
    {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();

        // Find GameSettingsPanel by searching all roots and their children
        GameObject settingsPanel = null;
        foreach (var root in roots)
        {
            // root might be Canvas itself, or Canvas might be nested
            var t = root.transform.Find("LobbyPanel/GameSettingsPanel")
                 ?? root.transform.Find("Canvas/LobbyPanel/GameSettingsPanel");
            if (t != null) { settingsPanel = t.gameObject; break; }
        }

        if (settingsPanel == null)
        {
            Debug.LogError("[WireBaseUids] GameSettingsPanel not found. Run WirePhase2UI first.");
            return;
        }

        var comp = settingsPanel.GetComponent<GameSettingsPanel>();
        if (comp == null)
        {
            Debug.LogError("[WireBaseUids] GameSettingsPanel component missing.");
            return;
        }

        // Add rows only if not already wired
        var a0 = GetField<TMP_InputField>(comp, "alliance0BaseField");
        var a1 = GetField<TMP_InputField>(comp, "alliance1BaseField");

        if (a0 == null)
        {
            a0 = CreateUidInputRow(settingsPanel.transform, "Alliance 1 Base UID");
            SetField(comp, "alliance0BaseField", a0);
            Debug.Log("[WireBaseUids] Added Alliance 1 base UID row.");
        }

        if (a1 == null)
        {
            a1 = CreateUidInputRow(settingsPanel.transform, "Alliance 2 Base UID");
            SetField(comp, "alliance1BaseField", a1);
            Debug.Log("[WireBaseUids] Added Alliance 2 base UID row.");
        }

        EditorUtility.SetDirty(settingsPanel);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log("[WireBaseUids] Done — scene saved.");
    }

    static TMP_InputField CreateUidInputRow(Transform parent, string labelText)
    {
        var row = new GameObject(labelText + "Row");
        var rowRT = row.AddComponent<RectTransform>();
        row.transform.SetParent(parent, false);
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 6;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlHeight     = true;
        var rowLE = row.AddComponent<LayoutElement>();
        rowLE.preferredHeight = 26;

        // Label
        var labelGO = new GameObject("Label");
        labelGO.AddComponent<RectTransform>();
        labelGO.transform.SetParent(row.transform, false);
        var labelTMP = labelGO.AddComponent<TextMeshProUGUI>();
        labelTMP.text     = labelText + ":";
        labelTMP.fontSize = 13;
        labelTMP.color    = Color.white;
        var labelLE = labelGO.AddComponent<LayoutElement>();
        labelLE.preferredWidth = 140;

        // InputField container
        var fieldGO = new GameObject("InputField");
        fieldGO.AddComponent<RectTransform>();
        fieldGO.transform.SetParent(row.transform, false);
        fieldGO.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 1f);
        var fieldLE = fieldGO.AddComponent<LayoutElement>();
        fieldLE.flexibleWidth = 1;

        // Text Area
        var textArea = new GameObject("Text Area");
        var taRT = textArea.AddComponent<RectTransform>();
        textArea.transform.SetParent(fieldGO.transform, false);
        textArea.AddComponent<RectMask2D>();
        taRT.anchorMin = Vector2.zero; taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(4, 2); taRT.offsetMax = new Vector2(-4, -2);

        // Text
        var textGO = new GameObject("Text");
        textGO.AddComponent<RectTransform>();
        textGO.transform.SetParent(textArea.transform, false);
        var textTMP = textGO.AddComponent<TextMeshProUGUI>();
        textTMP.text = "";
        textTMP.fontSize = 13;
        textTMP.color = Color.white;
        var textRT = textGO.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero; textRT.offsetMax = Vector2.zero;

        // Placeholder
        var phGO = new GameObject("Placeholder");
        phGO.AddComponent<RectTransform>();
        phGO.transform.SetParent(textArea.transform, false);
        var phTMP = phGO.AddComponent<TextMeshProUGUI>();
        phTMP.text = "scan tag to get UID";
        phTMP.fontSize = 13;
        phTMP.color = new Color(0.45f, 0.45f, 0.45f, 1f);
        phTMP.fontStyle = FontStyles.Italic;
        var phRT = phGO.GetComponent<RectTransform>();
        phRT.anchorMin = Vector2.zero; phRT.anchorMax = Vector2.one;
        phRT.offsetMin = Vector2.zero; phRT.offsetMax = Vector2.zero;

        var inputField = fieldGO.AddComponent<TMP_InputField>();
        inputField.textComponent = textTMP;
        inputField.placeholder   = phTMP;
        inputField.text          = "";
        inputField.contentType   = TMP_InputField.ContentType.Standard;

        return inputField;
    }

    static T GetField<T>(object target, string name) where T : class
    {
        var fi = FindField(target.GetType(), name);
        return fi?.GetValue(target) as T;
    }

    static void SetField(object target, string name, object value)
    {
        var fi = FindField(target.GetType(), name);
        if (fi == null) { Debug.LogWarning($"[WireBaseUids] Field '{name}' not found."); return; }
        fi.SetValue(target, value);
    }

    static FieldInfo FindField(System.Type t, string name)
    {
        while (t != null)
        {
            var fi = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi != null) return fi;
            t = t.BaseType;
        }
        return null;
    }
}
