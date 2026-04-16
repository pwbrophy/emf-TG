using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using TMPro;
using System.Reflection;

/// <summary>
/// Wires Phase 2 UI: GameSettingsPanel on LobbyPanel, RobotHpPanel + MatchTimerDisplay on
/// PlayingPanel, and EndedPanelPresenter on EndedPanel.
/// </summary>
public static class WirePhase2UI
{
    public static void Execute()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();

        GameObject canvas = null;
        foreach (var r in roots)
            if (r.name == "Canvas") { canvas = r; break; }

        if (canvas == null) { Debug.LogError("[WirePhase2UI] Canvas not found."); return; }

        var lobbyPanel   = canvas.transform.Find("LobbyPanel")?.gameObject;
        var playingPanel = canvas.transform.Find("PlayingPanel")?.gameObject;
        var endedPanel   = canvas.transform.Find("EndedPanel")?.gameObject;

        if (lobbyPanel   == null) { Debug.LogError("[WirePhase2UI] LobbyPanel not found.");   return; }
        if (playingPanel == null) { Debug.LogError("[WirePhase2UI] PlayingPanel not found."); return; }
        if (endedPanel   == null) { Debug.LogError("[WirePhase2UI] EndedPanel not found.");   return; }

        // ── 1. GameSettingsPanel on LobbyPanel ──────────────────────────────────────────
        SetupGameSettingsPanel(lobbyPanel);

        // ── 2. MatchTimerDisplay on PlayingPanel ────────────────────────────────────────
        SetupMatchTimerDisplay(playingPanel);

        // ── 3. RobotHpPanel on PlayingPanel ────────────────────────────────────────────
        SetupRobotHpPanel(playingPanel);

        // ── 4. EndedPanelPresenter on EndedPanel ────────────────────────────────────────
        SetupEndedPanelPresenter(endedPanel);

        EditorUtility.SetDirty(canvas);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log("[WirePhase2UI] Done — all Phase 2 UI wired and scene saved.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    static void SetupGameSettingsPanel(GameObject lobbyPanel)
    {
        // Container object
        var settingsGO = new GameObject("GameSettingsPanel", typeof(RectTransform), typeof(VerticalLayoutGroup));
        settingsGO.transform.SetParent(lobbyPanel.transform, false);

        var rt = settingsGO.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0.25f);
        rt.offsetMin = new Vector2(10f, 10f);
        rt.offsetMax = new Vector2(-10f, 0f);

        var vlg = settingsGO.GetComponent<VerticalLayoutGroup>();
        vlg.spacing            = 4;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlHeight     = true;
        vlg.padding = new RectOffset(4, 4, 4, 4);

        // Header label
        var headerGO = new GameObject("Header", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        headerGO.transform.SetParent(settingsGO.transform, false);
        var headerTMP = headerGO.GetComponent<TextMeshProUGUI>();
        headerTMP.text      = "Game Settings";
        headerTMP.fontSize  = 16;
        headerTMP.fontStyle = FontStyles.Bold;
        headerTMP.color     = Color.white;
        var headerLE = headerGO.AddComponent<LayoutElement>();
        headerLE.preferredHeight = 22;

        // Three input rows: MaxHp, DamagePerHit, Duration
        var maxHpField    = CreateLabeledInputRow(settingsGO.transform, "Max HP",          "100");
        var damageField   = CreateLabeledInputRow(settingsGO.transform, "Damage/Hit",      "25");
        var durationField = CreateLabeledInputRow(settingsGO.transform, "Duration (sec)",  "180");

        // Attach GameSettingsPanel component and wire fields
        var comp = settingsGO.AddComponent<GameSettingsPanel>();
        SetPrivateField(comp, "maxHpField",    maxHpField);
        SetPrivateField(comp, "damageField",   damageField);
        SetPrivateField(comp, "durationField", durationField);

        Debug.Log("[WirePhase2UI] GameSettingsPanel wired on LobbyPanel.");
    }

    static TMP_InputField CreateLabeledInputRow(Transform parent, string labelText, string defaultValue)
    {
        var row = new GameObject(labelText + "Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        var hlg = row.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 6;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlHeight     = true;
        var rowLE = row.AddComponent<LayoutElement>();
        rowLE.preferredHeight = 26;

        // Label
        var labelGO = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        labelGO.transform.SetParent(row.transform, false);
        var labelTMP = labelGO.GetComponent<TextMeshProUGUI>();
        labelTMP.text     = labelText + ":";
        labelTMP.fontSize = 13;
        labelTMP.color    = Color.white;
        var labelLE = labelGO.AddComponent<LayoutElement>();
        labelLE.preferredWidth = 110;

        // InputField
        var fieldGO = new GameObject("InputField", typeof(RectTransform), typeof(Image));
        fieldGO.transform.SetParent(row.transform, false);
        fieldGO.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 1f);
        var fieldLE = fieldGO.AddComponent<LayoutElement>();
        fieldLE.flexibleWidth = 1;

        var textArea = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textArea.transform.SetParent(fieldGO.transform, false);
        var taRT = textArea.GetComponent<RectTransform>();
        taRT.anchorMin = Vector2.zero; taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(4, 2); taRT.offsetMax = new Vector2(-4, -2);

        var textGO = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(textArea.transform, false);
        var textTMP = textGO.GetComponent<TextMeshProUGUI>();
        textTMP.text = defaultValue;
        textTMP.fontSize = 13;
        textTMP.color = Color.white;
        var textRT = textGO.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero; textRT.offsetMax = Vector2.zero;

        var placeholderGO = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        placeholderGO.transform.SetParent(textArea.transform, false);
        var phTMP = placeholderGO.GetComponent<TextMeshProUGUI>();
        phTMP.text = defaultValue;
        phTMP.fontSize = 13;
        phTMP.color = new Color(0.5f, 0.5f, 0.5f, 1f);
        var phRT = placeholderGO.GetComponent<RectTransform>();
        phRT.anchorMin = Vector2.zero; phRT.anchorMax = Vector2.one;
        phRT.offsetMin = Vector2.zero; phRT.offsetMax = Vector2.zero;

        var inputField = fieldGO.AddComponent<TMP_InputField>();
        inputField.textComponent   = textTMP;
        inputField.placeholder     = phTMP;
        inputField.text            = defaultValue;
        inputField.contentType     = TMP_InputField.ContentType.DecimalNumber;

        return inputField;
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    static void SetupMatchTimerDisplay(GameObject playingPanel)
    {
        // Timer label
        var timerGO = new GameObject("TimerLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        timerGO.transform.SetParent(playingPanel.transform, false);
        var timerTMP = timerGO.GetComponent<TextMeshProUGUI>();
        timerTMP.text      = "3:00";
        timerTMP.fontSize  = 36;
        timerTMP.fontStyle = FontStyles.Bold;
        timerTMP.color     = Color.white;
        timerTMP.alignment = TextAlignmentOptions.Center;
        var timerRT = timerGO.GetComponent<RectTransform>();
        timerRT.anchorMin        = new Vector2(0.5f, 1f);
        timerRT.anchorMax        = new Vector2(0.5f, 1f);
        timerRT.pivot            = new Vector2(0.5f, 1f);
        timerRT.anchoredPosition = new Vector2(0f, -10f);
        timerRT.sizeDelta        = new Vector2(120f, 50f);

        // Component on panel
        var comp = playingPanel.AddComponent<MatchTimerDisplay>();
        SetPrivateField(comp, "timerLabel", timerTMP);

        Debug.Log("[WirePhase2UI] MatchTimerDisplay wired on PlayingPanel.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    static void SetupRobotHpPanel(GameObject playingPanel)
    {
        // Scrollable container in bottom-right of playing panel
        var panelGO = new GameObject("RobotHpPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        panelGO.transform.SetParent(playingPanel.transform, false);
        panelGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);
        var panelRT = panelGO.GetComponent<RectTransform>();
        panelRT.anchorMin        = new Vector2(1f, 0f);
        panelRT.anchorMax        = new Vector2(1f, 0.6f);
        panelRT.pivot            = new Vector2(1f, 0f);
        panelRT.anchoredPosition = new Vector2(-10f, 10f);
        panelRT.sizeDelta        = new Vector2(220f, 0f);

        // Content container (VerticalLayoutGroup)
        var containerGO = new GameObject("RowContainer", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        containerGO.transform.SetParent(panelGO.transform, false);
        var containerRT = containerGO.GetComponent<RectTransform>();
        containerRT.anchorMin = Vector2.zero;
        containerRT.anchorMax = Vector2.one;
        containerRT.offsetMin = new Vector2(4, 4);
        containerRT.offsetMax = new Vector2(-4, -4);
        var vlg = containerGO.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 4;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlHeight     = true;
        var csf = containerGO.GetComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // HP panel header
        var hdrGO = new GameObject("Header", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        hdrGO.transform.SetParent(containerGO.transform, false);
        var hdrTMP = hdrGO.GetComponent<TextMeshProUGUI>();
        hdrTMP.text      = "HP";
        hdrTMP.fontSize  = 13;
        hdrTMP.fontStyle = FontStyles.Bold;
        hdrTMP.color     = Color.white;
        var hdrLE = hdrGO.AddComponent<LayoutElement>();
        hdrLE.preferredHeight = 18;

        // Attach component
        var comp = panelGO.AddComponent<RobotHpPanel>();
        SetPrivateField(comp, "rowContainer", containerRT);

        Debug.Log("[WirePhase2UI] RobotHpPanel wired on PlayingPanel.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    static void SetupEndedPanelPresenter(GameObject endedPanel)
    {
        // Result label
        var resultGO = new GameObject("ResultLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        resultGO.transform.SetParent(endedPanel.transform, false);
        var resultTMP = resultGO.GetComponent<TextMeshProUGUI>();
        resultTMP.text      = "Game Over";
        resultTMP.fontSize  = 42;
        resultTMP.fontStyle = FontStyles.Bold;
        resultTMP.color     = Color.white;
        resultTMP.alignment = TextAlignmentOptions.Center;
        var resultRT = resultGO.GetComponent<RectTransform>();
        resultRT.anchorMin        = new Vector2(0.1f, 0.4f);
        resultRT.anchorMax        = new Vector2(0.9f, 0.8f);
        resultRT.offsetMin        = Vector2.zero;
        resultRT.offsetMax        = Vector2.zero;

        // Presenter component
        var comp = endedPanel.AddComponent<EndedPanelPresenter>();
        SetPrivateField(comp, "resultLabel", resultTMP);

        Debug.Log("[WirePhase2UI] EndedPanelPresenter wired on EndedPanel.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    static void SetPrivateField(object target, string fieldName, object value)
    {
        var t = target.GetType();
        FieldInfo fi = null;
        while (fi == null && t != null)
        {
            fi = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            t = t.BaseType;
        }
        if (fi == null) { Debug.LogWarning($"[WirePhase2UI] Field '{fieldName}' not found on {target.GetType().Name}"); return; }
        fi.SetValue(target, value);
    }
}
