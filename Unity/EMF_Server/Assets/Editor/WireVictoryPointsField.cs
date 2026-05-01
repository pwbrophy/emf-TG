using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Reflection;

/// <summary>
/// Adds a "Victory Points" input row to GameSettingsPanel and wires it to
/// GameSettingsPanel.maxTeamPointsField.
/// Menu: Thundergeddon → Wire Victory Points Field
/// Safe to re-run — skips the row if already wired.
/// </summary>
public static class WireVictoryPointsField
{
    [MenuItem("Thundergeddon/Wire Victory Points Field")]
    public static void Execute()
    {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();

        GameObject settingsPanel = null;
        foreach (var root in roots)
        {
            var t = root.transform.Find("LobbyPanel/GameSettingsPanel")
                 ?? root.transform.Find("Canvas/LobbyPanel/GameSettingsPanel");
            if (t != null) { settingsPanel = t.gameObject; break; }
        }

        if (settingsPanel == null)
        {
            Debug.LogError("[WireVictoryPointsField] GameSettingsPanel not found.");
            return;
        }

        var comp = settingsPanel.GetComponent<GameSettingsPanel>();
        if (comp == null)
        {
            Debug.LogError("[WireVictoryPointsField] GameSettingsPanel component missing.");
            return;
        }

        var existing = GetField<TMP_InputField>(comp, "maxTeamPointsField");
        if (existing != null)
        {
            Debug.Log("[WireVictoryPointsField] maxTeamPointsField already wired — nothing to do.");
            return;
        }

        var field = CreateInputRow(settingsPanel.transform, "Victory Points");
        SetField(comp, "maxTeamPointsField", field);
        Debug.Log("[WireVictoryPointsField] Added Victory Points row.");

        EditorUtility.SetDirty(settingsPanel);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log("[WireVictoryPointsField] Done — scene saved.");
    }

    static TMP_InputField CreateInputRow(Transform parent, string labelText)
    {
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");

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
        var labelRT = labelGO.AddComponent<RectTransform>();
        labelGO.transform.SetParent(row.transform, false);
        var labelTMP = labelGO.AddComponent<TextMeshProUGUI>();
        labelTMP.text     = labelText + ":";
        labelTMP.fontSize = 13;
        labelTMP.color    = Color.white;
        if (font) labelTMP.font = font;
        var labelLE = labelGO.AddComponent<LayoutElement>();
        labelLE.preferredWidth = 140;

        // InputField container
        var fieldGO = new GameObject("InputField");
        var fieldRT = fieldGO.AddComponent<RectTransform>();
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
        var textRT = textGO.AddComponent<RectTransform>();
        textGO.transform.SetParent(textArea.transform, false);
        var textTMP = textGO.AddComponent<TextMeshProUGUI>();
        textTMP.text = "";
        textTMP.fontSize = 13;
        textTMP.color = Color.white;
        if (font) textTMP.font = font;
        textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero; textRT.offsetMax = Vector2.zero;

        // Placeholder
        var phGO = new GameObject("Placeholder");
        var phRT = phGO.AddComponent<RectTransform>();
        phGO.transform.SetParent(textArea.transform, false);
        var phTMP = phGO.AddComponent<TextMeshProUGUI>();
        phTMP.text = "300";
        phTMP.fontSize = 13;
        phTMP.color = new Color(0.45f, 0.45f, 0.45f, 1f);
        phTMP.fontStyle = FontStyles.Italic;
        if (font) phTMP.font = font;
        phRT.anchorMin = Vector2.zero; phRT.anchorMax = Vector2.one;
        phRT.offsetMin = Vector2.zero; phRT.offsetMax = Vector2.zero;

        var inputField = fieldGO.AddComponent<TMP_InputField>();
        inputField.textComponent  = textTMP;
        inputField.placeholder    = phTMP;
        inputField.text           = "";
        inputField.contentType    = TMP_InputField.ContentType.IntegerNumber;

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
        if (fi == null) { Debug.LogWarning($"[WireVictoryPointsField] Field '{name}' not found."); return; }
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
