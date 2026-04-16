using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Replaces all legacy UI text/input/dropdown elements in the Thundergeddon scene
/// with TMP equivalents, then wires all component references.
/// Run from the Editor: execute_script with method Execute.
/// </summary>
public class WireScene
{
    public static void Execute()
    {
        // ---- helpers ----
        static T RequireGO<T>(string path) where T : Component
        {
            var go = GameObject.Find(path);
            if (go == null) { Debug.LogError($"[WireScene] Missing GO: {path}"); return null; }
            var c = go.GetComponent<T>();
            if (c == null) Debug.LogWarning($"[WireScene] No {typeof(T).Name} on {path}");
            return c;
        }

        static GameObject RequireGO2(string path)
        {
            var go = GameObject.Find(path);
            if (go == null) Debug.LogError($"[WireScene] Missing GO: {path}");
            return go;
        }

        static TextMeshProUGUI ReplaceLegacyText(string path)
        {
            var go = GameObject.Find(path);
            if (go == null) { Debug.LogError($"[WireScene] Missing text GO: {path}"); return null; }
            // Remove legacy Text if present
            var leg = go.GetComponent<Text>();
            if (leg != null) Object.DestroyImmediate(leg);
            var cr = go.GetComponent<CanvasRenderer>();
            // Add TMP
            var tmp = go.GetComponent<TextMeshProUGUI>() ?? go.AddComponent<TextMeshProUGUI>();
            tmp.text = "";
            tmp.fontSize = 18;
            tmp.color = Color.white;
            return tmp;
        }

        static TMP_InputField ReplaceLegacyInputField(string inputPath)
        {
            var go = GameObject.Find(inputPath);
            if (go == null) { Debug.LogError($"[WireScene] Missing inputfield GO: {inputPath}"); return null; }

            // Remove legacy InputField
            var legIf = go.GetComponent<InputField>();
            if (legIf != null) Object.DestroyImmediate(legIf);

            // Remove legacy text child if present
            var legText = go.transform.Find("Text");
            if (legText != null) Object.DestroyImmediate(legText.gameObject);

            // Create TMP text child
            var textGO = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textGO.transform.SetParent(go.transform, false);
            var rt = textGO.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(10, 6);
            rt.offsetMax = new Vector2(-10, -7);
            var textComp = textGO.GetComponent<TextMeshProUGUI>();
            textComp.fontSize = 16;
            textComp.color = Color.black;

            // Add TMP_InputField
            var tmpIf = go.GetComponent<TMP_InputField>() ?? go.AddComponent<TMP_InputField>();
            tmpIf.textComponent = textComp;
            tmpIf.textViewport = rt;
            return tmpIf;
        }

        static TMP_Dropdown ReplaceLegacyDropdown(string path)
        {
            var go = GameObject.Find(path);
            if (go == null) { Debug.LogError($"[WireScene] Missing dropdown GO: {path}"); return null; }

            // Remove legacy Dropdown
            var legDD = go.GetComponent<Dropdown>();
            if (legDD != null) Object.DestroyImmediate(legDD);

            // Build minimal TMP_Dropdown
            var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.2f, 0.2f, 1f);

            // Label child for display text
            var labelGO = go.transform.Find("Label")?.gameObject
                       ?? new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelGO.transform.SetParent(go.transform, false);
            var labelTMP = labelGO.GetComponent<TextMeshProUGUI>();
            labelTMP.fontSize = 16;
            labelTMP.color = Color.white;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0, 0); labelRT.anchorMax = new Vector2(1, 1);
            labelRT.offsetMin = new Vector2(10, 2); labelRT.offsetMax = new Vector2(-28, -2);

            // Template child (required by TMP_Dropdown — can be minimal)
            var templateGO = go.transform.Find("Template")?.gameObject
                          ?? new GameObject("Template", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
            templateGO.transform.SetParent(go.transform, false);
            templateGO.SetActive(false);

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.transform.SetParent(templateGO.transform, false);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);

            var item = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
            item.transform.SetParent(content.transform, false);

            var itemLabelGO = new GameObject("Item Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            itemLabelGO.transform.SetParent(item.transform, false);
            var itemLabelTMP = itemLabelGO.GetComponent<TextMeshProUGUI>();
            itemLabelTMP.fontSize = 16;
            itemLabelTMP.color = Color.black;

            // ScrollRect setup
            var sr = templateGO.GetComponent<ScrollRect>();
            sr.content = content.GetComponent<RectTransform>();
            sr.viewport = viewport.GetComponent<RectTransform>();

            // TMP_Dropdown
            var dd = go.GetComponent<TMP_Dropdown>() ?? go.AddComponent<TMP_Dropdown>();
            dd.captionText = labelTMP;
            dd.itemText = itemLabelTMP;
            dd.template = templateGO.GetComponent<RectTransform>();

            return dd;
        }

        // ================================================================
        // LOBBY PANEL — replace legacy elements
        // ================================================================
        var numRobotsText    = ReplaceLegacyText("Canvas/LobbyPanel/NumRobotsText");
        var playersDropdown  = ReplaceLegacyDropdown("Canvas/LobbyPanel/PlayersDropdown");
        var allianceDropdown = ReplaceLegacyDropdown("Canvas/LobbyPanel/AllianceDropdown");
        var playerNameField  = ReplaceLegacyInputField("Canvas/LobbyPanel/PlayerNameField");

        // ================================================================
        // PLAYING PANEL — replace legacy text labels
        // ================================================================
        var robotNameLabel     = ReplaceLegacyText("Canvas/PlayingPanel/RobotNameLabel");
        var robotIpLabel       = ReplaceLegacyText("Canvas/PlayingPanel/RobotIpLabel");
        var robotPlayerLabel   = ReplaceLegacyText("Canvas/PlayingPanel/RobotPlayerLabel");
        var robotAllianceLabel = ReplaceLegacyText("Canvas/PlayingPanel/RobotAllianceLabel");
        var robotClientLabel   = ReplaceLegacyText("Canvas/PlayingPanel/RobotClientLabel");
        var shootResultLabel   = ReplaceLegacyText("Canvas/PlayingPanel/ShootResultLabel");
        var cooldownLabel      = ReplaceLegacyText("Canvas/PlayingPanel/CooldownLabel");

        // ================================================================
        // Wire ServerPanelPresenter
        // ================================================================
        var serverPP = RequireGO<ServerPanelPresenter>("Canvas/LobbyPanel");
        // NumRobots is a GameObject field
        if (serverPP != null)
        {
            var t = typeof(ServerPanelPresenter);
            var f = t.GetField("NumRobots",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            f?.SetValue(serverPP, RequireGO2("Canvas/LobbyPanel/NumRobotsText"));
            EditorUtility.SetDirty(serverPP);
        }

        // ================================================================
        // Wire DevRobotsToolbar
        // ================================================================
        var devToolbar = RequireGO<DevRobotsToolbar>("Canvas/LobbyPanel");
        if (devToolbar != null)
        {
            SetField(devToolbar, "addFakeRobotButton", RequireGO<Button>("Canvas/LobbyPanel/AddFakeRobotButton"));
            SetField(devToolbar, "removeLastButton",   RequireGO<Button>("Canvas/LobbyPanel/RemoveLastRobotButton"));
            EditorUtility.SetDirty(devToolbar);
        }

        // ================================================================
        // Wire PlayersEditorPanel
        // ================================================================
        var playerEditor = RequireGO<PlayersEditorPanel>("Canvas/LobbyPanel");
        if (playerEditor != null)
        {
            SetField(playerEditor, "playersDropdown",  playersDropdown);
            SetField(playerEditor, "allianceDropdown", allianceDropdown);
            SetField(playerEditor, "nameField",        playerNameField);
            SetField(playerEditor, "addButton",    RequireGO<Button>("Canvas/LobbyPanel/AddPlayerButton"));
            SetField(playerEditor, "removeButton", RequireGO<Button>("Canvas/LobbyPanel/RemovePlayerButton"));
            EditorUtility.SetDirty(playerEditor);
        }

        // ================================================================
        // Wire RobotsPanelPresenter — content is Viewport/Content inside the scrollview
        // ================================================================
        var robotsPP = RequireGO<RobotsPanelPresenter>("Canvas/LobbyPanel");
        if (robotsPP != null)
        {
            var scrollContent = GameObject.Find("Canvas/LobbyPanel/RobotsScrollView/Viewport/Content");
            if (scrollContent != null)
            {
                SetField(robotsPP, "content", scrollContent.GetComponent<RectTransform>());
            }
            else
            {
                Debug.LogWarning("[WireScene] RobotsScrollView/Viewport/Content not found - rowPrefab must be assigned manually.");
            }
            EditorUtility.SetDirty(robotsPP);
        }

        // ================================================================
        // Wire GamePanelPresenter
        // ================================================================
        var gamePP = RequireGO<GamePanelPresenter>("Canvas/PlayingPanel");
        if (gamePP != null)
        {
            SetField(gamePP, "endGameButton", RequireGO<Button>("Canvas/PlayingPanel/EndGameButton"));
            EditorUtility.SetDirty(gamePP);
        }

        // ================================================================
        // Wire RobotSelectionPanel
        // ================================================================
        var selPanel = RequireGO<RobotSelectionPanel>("Canvas/PlayingPanel");
        if (selPanel != null)
        {
            SetField(selPanel, "prevButton",    RequireGO<Button>("Canvas/PlayingPanel/PrevRobotButton"));
            SetField(selPanel, "nextButton",    RequireGO<Button>("Canvas/PlayingPanel/NextRobotButton"));
            SetField(selPanel, "clearButton",   RequireGO<Button>("Canvas/PlayingPanel/ClearRobotButton"));
            SetField(selPanel, "nameLabel",     robotNameLabel);
            SetField(selPanel, "ipLabel",       robotIpLabel);
            SetField(selPanel, "playerLabel",   robotPlayerLabel);
            SetField(selPanel, "allianceLabel", robotAllianceLabel);
            SetField(selPanel, "clientLabel",   robotClientLabel);
            // video receiver lives on Servers
            var serversGO = GameObject.Find("Servers");
            if (serversGO != null)
                SetField(selPanel, "video", serversGO.GetComponent<ESP32VideoReceiver>());
            EditorUtility.SetDirty(selPanel);
        }

        // ================================================================
        // Wire RobotControlPanel (on JoystickBase)
        // ================================================================
        var joystickBaseGO = GameObject.Find("Canvas/PlayingPanel/JoystickBase");
        var controlPanel   = joystickBaseGO?.GetComponent<RobotControlPanel>();
        if (controlPanel != null)
        {
            SetField(controlPanel, "baseRect",   joystickBaseGO.GetComponent<RectTransform>());
            SetField(controlPanel, "handleRect",
                GameObject.Find("Canvas/PlayingPanel/JoystickBase/JoystickHandle")?.GetComponent<RectTransform>());
            var turretSlider = GameObject.Find("Canvas/PlayingPanel/TurretSlider")?.GetComponent<Slider>();
            SetField(controlPanel, "turretSlider",  turretSlider);
            SetField(controlPanel, "selectionPanel", selPanel);
            EditorUtility.SetDirty(controlPanel);
        }

        // ================================================================
        // Wire ShootingController
        // ================================================================
        var shootCtrl = RequireGO<ShootingController>("Canvas/PlayingPanel");
        if (shootCtrl != null)
        {
            SetField(shootCtrl, "shootButton",   RequireGO<Button>("Canvas/PlayingPanel/ShootButton"));
            SetField(shootCtrl, "resultLabel",   shootResultLabel);
            SetField(shootCtrl, "cooldownLabel", cooldownLabel);
            SetField(shootCtrl, "selectionPanel", selPanel);
            EditorUtility.SetDirty(shootCtrl);
        }

        // ================================================================
        // Save
        // ================================================================
        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
        Debug.Log("[WireScene] Done.");
    }

    // Reflection helper — sets a private serialized field
    static void SetField(object target, string fieldName, object value)
    {
        var t = target.GetType();
        System.Reflection.FieldInfo f = null;
        while (f == null && t != null)
        {
            f = t.GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            t = t.BaseType;
        }
        if (f == null)
        {
            Debug.LogWarning($"[WireScene] Field '{fieldName}' not found on {target.GetType().Name}");
            return;
        }
        f.SetValue(target, value);
    }
}
