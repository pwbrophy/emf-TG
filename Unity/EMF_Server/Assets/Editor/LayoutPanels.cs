using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEditor;

/// <summary>
/// Lays out all UI panels. Safe to re-run: only moves/resizes existing objects
/// and assigns fonts to TMP labels. Also fixes the robot scroll view content.
/// </summary>
public static class LayoutPanels
{
    // ── Font ─────────────────────────────────────────────────────────────────────────
    static TMP_FontAsset _font;
    static TMP_FontAsset Font()
    {
        if (_font != null) return _font;
        _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
        if (_font == null) _font = TMP_Settings.defaultFontAsset;
        return _font;
    }

    static TextMeshProUGUI StyleTMP(GameObject go, float size, FontStyles style,
                                     Color color, TextAlignmentOptions align = TextAlignmentOptions.Center)
    {
        if (go == null) return null;
        var tmp = go.GetComponent<TextMeshProUGUI>() ?? go.AddComponent<TextMeshProUGUI>();
        tmp.fontSize  = size;
        tmp.fontStyle = style;
        tmp.color     = color;
        tmp.alignment = align;
        var f = Font(); if (f != null) tmp.font = f;
        return tmp;
    }

    // Get or create a child TMP label — adds components sequentially so TMP wires CanvasRenderer correctly
    static GameObject EnsureLabel(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) return t.gameObject;
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        // Do NOT manually add CanvasRenderer — TMP's [RequireComponent] handles it
        go.AddComponent<TextMeshProUGUI>();
        return go;
    }

    // ── RectTransform ────────────────────────────────────────────────────────────────
    static void Stretch(GameObject go, float xMin, float yMin, float xMax, float yMax,
                        float l = 0, float b = 0, float r = 0, float t = 0)
    {
        var rt        = go.GetComponent<RectTransform>();
        rt.anchorMin  = new Vector2(xMin, yMin);
        rt.anchorMax  = new Vector2(xMax, yMax);
        rt.pivot      = new Vector2(0.5f, 0.5f);
        rt.offsetMin  = new Vector2(l, b);
        rt.offsetMax  = new Vector2(-r, -t);
    }

    static void Fixed(GameObject go, float ax, float ay, float px, float py,
                      float x, float y, float w, float h)
    {
        var rt              = go.GetComponent<RectTransform>();
        rt.anchorMin        = new Vector2(ax, ay);
        rt.anchorMax        = new Vector2(ax, ay);
        rt.pivot            = new Vector2(px, py);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta        = new Vector2(w, h);
    }

    // ── Visuals ──────────────────────────────────────────────────────────────────────
    static Image BG(GameObject go, float r, float g, float b, float a = 1f)
    {
        var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
        img.color = new Color(r, g, b, a);
        return img;
    }

    static void ButtonStyle(GameObject btn, Color bg, string label, float fontSize)
    {
        BG(btn, bg.r, bg.g, bg.b, bg.a);
        var tmp = btn.GetComponentInChildren<TextMeshProUGUI>(true);
        if (tmp == null) return;
        tmp.text      = label;
        tmp.fontSize  = fontSize;
        tmp.color     = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        var f = Font(); if (f != null) tmp.font = f;
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    public static void Execute()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        GameObject canvas = null;
        foreach (var r in scene.GetRootGameObjects())
            if (r.name == "Canvas") { canvas = r; break; }
        if (canvas == null) { Debug.LogError("[Layout] Canvas not found."); return; }

        // NOTE: no Image on canvas root — panels handle their own BG
        // Remove any stale canvas Image that might cover children
        var canvasImg = canvas.GetComponent<Image>();
        if (canvasImg != null) canvasImg.color = new Color(0, 0, 0, 0);

        GameObject GO(string p) { var tr = canvas.transform.Find(p); return tr ? tr.gameObject : null; }

        // Temporarily activate all panels so TMP components initialise properly,
        // then restore original active states.
        var panels = new[] { "MainMenuPanel", "LobbyPanel", "PlayingPanel", "EndedPanel" };
        var wasActive = new bool[panels.Length];
        for (int i = 0; i < panels.Length; i++)
        {
            var go = GO(panels[i]);
            if (go == null) continue;
            wasActive[i] = go.activeSelf;
            go.SetActive(true);
        }

        LayoutMainMenu(GO("MainMenuPanel"), GO("MainMenuPanel/ToLobbyButton"));
        LayoutLobby(canvas, GO);
        LayoutPlaying(canvas, GO);
        LayoutEnded(GO("EndedPanel"), GO("EndedPanel/BackToMenuButton"), GO("EndedPanel/ResultLabel"));

        // Restore original active state
        for (int i = 0; i < panels.Length; i++)
        {
            var go = GO(panels[i]);
            if (go != null) go.SetActive(wasActive[i]);
        }

        // Fix fonts on ALL TMP components in the scene
        FixAllFonts(canvas);

        EditorUtility.SetDirty(canvas);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log("[Layout] Done.");
    }

    static void FixAllFonts(GameObject root)
    {
        var font = Font();
        if (font == null) return;
        foreach (var tmp in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (tmp.font == null) tmp.font = font;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    //  MAIN MENU
    // ═══════════════════════════════════════════════════════════════════════════════
    static void LayoutMainMenu(GameObject panel, GameObject lobbyBtn)
    {
        if (panel == null) return;
        BG(panel, 0.06f, 0.06f, 0.10f);

        var title = EnsureLabel(panel.transform, "TitleLabel");
        StyleTMP(title, 56, FontStyles.Bold, new Color(1f, 0.8f, 0.1f));
        var titleTMP = title.GetComponent<TextMeshProUGUI>();
        titleTMP.text = "THUNDERGEDDON";
        Stretch(title, 0.05f, 0.60f, 0.95f, 0.88f);

        var sub = EnsureLabel(panel.transform, "SubLabel");
        StyleTMP(sub, 18, FontStyles.Normal, new Color(0.7f, 0.7f, 0.7f));
        sub.GetComponent<TextMeshProUGUI>().text = "Tank Battle · EMF Camp";
        Stretch(sub, 0.1f, 0.50f, 0.9f, 0.62f);

        if (lobbyBtn != null)
        {
            ButtonStyle(lobbyBtn, new Color(0.18f, 0.45f, 0.72f), "Go to Lobby", 22);
            Fixed(lobbyBtn, 0.5f, 0.35f, 0.5f, 0.5f, 0f, 0f, 220f, 64f);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    //  LOBBY
    // ═══════════════════════════════════════════════════════════════════════════════
    static void LayoutLobby(GameObject canvas, System.Func<string, GameObject> GO)
    {
        var panel = GO("LobbyPanel");
        if (panel == null) return;
        BG(panel, 0.07f, 0.07f, 0.11f);

        // Title
        var title = EnsureLabel(panel.transform, "TitleLabel");
        StyleTMP(title, 20, FontStyles.Bold, new Color(1f, 0.8f, 0.1f));
        title.GetComponent<TextMeshProUGUI>().text = "THUNDERGEDDON  —  LOBBY";
        Stretch(title, 0f, 0.93f, 1f, 1f, 12f, 2f, 12f, 2f);

        // ── LEFT — Robots ──────────────────────────────────────────────────────────
        var secRobots = EnsureLabel(panel.transform, "RobotsLabel");
        StyleTMP(secRobots, 13, FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft);
        secRobots.GetComponent<TextMeshProUGUI>().text = "Connected Robots";
        Stretch(secRobots, 0f, 0.88f, 0.38f, 0.93f, 10f, 1f, 2f, 1f);

        var numRobots = GO("LobbyPanel/NumRobotsText");
        if (numRobots != null)
        {
            StyleTMP(numRobots, 13, FontStyles.Normal, new Color(0.6f, 0.8f, 0.6f), TextAlignmentOptions.MidlineRight);
            Stretch(numRobots, 0.38f, 0.88f, 0.5f, 0.93f, 0f, 1f, 10f, 1f);
        }

        var scrollView = GO("LobbyPanel/RobotsScrollView");
        if (scrollView != null)
        {
            Stretch(scrollView, 0f, 0.30f, 0.5f, 0.88f, 10f, 2f, 4f, 2f);
            BG(scrollView, 0.04f, 0.04f, 0.07f);
            FixScrollViewContent(scrollView);
        }

        // Add / Remove buttons — right next to each other
        var addFake    = GO("LobbyPanel/AddFakeRobotButton");
        var removeLast = GO("LobbyPanel/RemoveLastRobotButton");
        if (addFake    != null) { ButtonStyle(addFake,    new Color(0.15f, 0.38f, 0.15f), "Add Fake Robot", 12); Stretch(addFake,    0f,    0.22f, 0.25f, 0.30f, 10f, 2f, 2f, 2f); }
        if (removeLast != null) { ButtonStyle(removeLast, new Color(0.35f, 0.15f, 0.15f), "Remove Last",    12); Stretch(removeLast, 0.25f, 0.22f, 0.5f,  0.30f, 2f,  2f, 4f, 2f); }

        // Game Settings — bottom of left column
        var settingsPanel = GO("LobbyPanel/GameSettingsPanel");
        if (settingsPanel != null)
        {
            Stretch(settingsPanel, 0f, 0f, 0.5f, 0.22f, 10f, 4f, 4f, 2f);
            BG(settingsPanel, 0.10f, 0.10f, 0.16f, 0.95f);
        }

        // ── RIGHT — Players ────────────────────────────────────────────────────────
        var secPlayers = EnsureLabel(panel.transform, "PlayersLabel");
        StyleTMP(secPlayers, 13, FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft);
        secPlayers.GetComponent<TextMeshProUGUI>().text = "Players";
        Stretch(secPlayers, 0.50f, 0.88f, 1f, 0.93f, 4f, 1f, 10f, 1f);

        // Players scroll view (rows created at runtime by PlayersEditorPanel)
        var playersScrollView = GO("LobbyPanel/PlayersScrollView");
        if (playersScrollView != null)
        {
            Stretch(playersScrollView, 0.50f, 0.23f, 1f, 0.88f, 4f, 2f, 10f, 2f);
            BG(playersScrollView, 0.04f, 0.04f, 0.07f, 0.95f);
        }

        var addPlayer = GO("LobbyPanel/AddPlayerButton");
        if (addPlayer != null) { ButtonStyle(addPlayer, new Color(0.15f, 0.38f, 0.15f), "Add Player", 12); Stretch(addPlayer, 0.50f, 0.15f, 1f, 0.23f, 4f, 2f, 10f, 2f); }

        // Old individual-field controls are deactivated by WirePlayersPanel — skip layout.

        // Start Game — prominent green, bottom-right
        var startGame = GO("LobbyPanel/StartGameButton");
        if (startGame != null)
        {
            ButtonStyle(startGame, new Color(0.12f, 0.56f, 0.12f), "Start Game", 22);
            Stretch(startGame, 0.55f, 0.02f, 0.98f, 0.15f, 4f, 4f, 4f, 4f);
        }
    }

    static void FixScrollViewContent(GameObject scrollView)
    {
        var sr = scrollView.GetComponent<ScrollRect>() ?? scrollView.AddComponent<ScrollRect>();
        sr.vertical = true; sr.horizontal = false;

        var vp = scrollView.transform.Find("Viewport");
        if (vp == null) return;

        var vpRT = vp.GetComponent<RectTransform>();
        vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
        vpRT.offsetMin = Vector2.zero; vpRT.offsetMax = Vector2.zero;

        var mask = vp.GetComponent<Mask>() ?? vp.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;
        var maskImg = vp.GetComponent<Image>() ?? vp.gameObject.AddComponent<Image>();
        maskImg.color = new Color(1f, 1f, 1f, 0.01f);
        if (sr.viewport == null) sr.viewport = vpRT;

        var content = vp.Find("Content");
        if (content == null) return;
        var contentRT = content.GetComponent<RectTransform>();
        contentRT.anchorMin        = new Vector2(0f, 1f);
        contentRT.anchorMax        = new Vector2(1f, 1f);
        contentRT.pivot            = new Vector2(0.5f, 1f);
        contentRT.sizeDelta        = Vector2.zero;
        contentRT.anchoredPosition = Vector2.zero;
        if (sr.content == null) sr.content = contentRT;

        var vlg = content.GetComponent<VerticalLayoutGroup>() ?? content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 2; vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childControlHeight = true; vlg.childControlWidth = true;
        vlg.padding = new RectOffset(2, 2, 2, 2);

        var csf = content.GetComponent<ContentSizeFitter>() ?? content.gameObject.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    //  PLAYING
    // ═══════════════════════════════════════════════════════════════════════════════
    static void LayoutPlaying(GameObject canvas, System.Func<string, GameObject> GO)
    {
        var panel = GO("PlayingPanel");
        if (panel == null) return;
        BG(panel, 0.07f, 0.07f, 0.11f);

        // Timer — top-centre
        var timer = GO("PlayingPanel/TimerLabel");
        if (timer != null)
        {
            StyleTMP(timer, 42, FontStyles.Bold, Color.white);
            Stretch(timer, 0.3f, 0.89f, 0.7f, 1f, 0f, 2f, 0f, 4f);
        }

        // End Game — top-right red button
        var endGame = GO("PlayingPanel/EndGameButton");
        if (endGame != null)
        {
            ButtonStyle(endGame, new Color(0.55f, 0.10f, 0.10f), "End Game", 14);
            Fixed(endGame, 1f, 1f, 1f, 1f, -8f, -8f, 140f, 38f);
        }

        // ── Left — Robot info + selection + shooting ────────────────────────────
        var nameLabel     = GO("PlayingPanel/RobotNameLabel");
        var ipLabel       = GO("PlayingPanel/RobotIpLabel");
        var playerLabel   = GO("PlayingPanel/RobotPlayerLabel");
        var allianceLabel = GO("PlayingPanel/RobotAllianceLabel");
        var clientLabel   = GO("PlayingPanel/RobotClientLabel");

        StyleTMP(nameLabel,     14, FontStyles.Bold,   Color.white,                   TextAlignmentOptions.MidlineLeft);
        StyleTMP(ipLabel,       11, FontStyles.Normal, new Color(0.6f, 0.6f, 0.6f),   TextAlignmentOptions.MidlineLeft);
        StyleTMP(playerLabel,   11, FontStyles.Normal, new Color(0.6f, 0.85f, 1.0f),  TextAlignmentOptions.MidlineLeft);
        StyleTMP(allianceLabel, 11, FontStyles.Normal, new Color(1.0f, 0.85f, 0.4f),  TextAlignmentOptions.MidlineLeft);
        StyleTMP(clientLabel,   10, FontStyles.Normal, new Color(0.5f, 0.5f, 0.5f),   TextAlignmentOptions.MidlineLeft);

        float il = 8f, ir = 4f; // info margins
        Stretch(nameLabel,     0f, 0.83f, 0.37f, 0.89f, il, 1f, ir, 1f);
        Stretch(ipLabel,       0f, 0.78f, 0.37f, 0.83f, il, 1f, ir, 1f);
        Stretch(playerLabel,   0f, 0.73f, 0.37f, 0.78f, il, 1f, ir, 1f);
        Stretch(allianceLabel, 0f, 0.68f, 0.37f, 0.73f, il, 1f, ir, 1f);
        Stretch(clientLabel,   0f, 0.63f, 0.37f, 0.68f, il, 1f, ir, 1f);

        // Prev / Next / Clear — robot selection row
        var prev  = GO("PlayingPanel/PrevRobotButton");
        var next  = GO("PlayingPanel/NextRobotButton");
        var clear = GO("PlayingPanel/ClearRobotButton");
        if (prev  != null) { ButtonStyle(prev,  new Color(0.20f, 0.30f, 0.45f), "◄", 18); Stretch(prev,  0f,    0.57f, 0.11f, 0.63f, 8f, 2f, 2f, 2f); }
        if (next  != null) { ButtonStyle(next,  new Color(0.20f, 0.30f, 0.45f), "►", 18); Stretch(next,  0.11f, 0.57f, 0.22f, 0.63f, 2f, 2f, 2f, 2f); }
        if (clear != null) { ButtonStyle(clear, new Color(0.35f, 0.20f, 0.20f), "✕", 16); Stretch(clear, 0.22f, 0.57f, 0.30f, 0.63f, 2f, 2f, 4f, 2f); }

        // Joystick
        var joystick = GO("PlayingPanel/JoystickBase");
        if (joystick != null) Fixed(joystick, 0.13f, 0.30f, 0.5f, 0.5f, 0f, 0f, 140f, 140f);

        // Shoot + feedback labels
        var shoot  = GO("PlayingPanel/ShootButton");
        var result = GO("PlayingPanel/ShootResultLabel");
        var cool   = GO("PlayingPanel/CooldownLabel");
        if (shoot  != null) { ButtonStyle(shoot, new Color(0.70f, 0.18f, 0.05f), "FIRE!", 22); Stretch(shoot, 0f, 0.11f, 0.28f, 0.24f, 8f, 4f, 4f, 4f); }
        if (result != null) { StyleTMP(result, 12, FontStyles.Normal, new Color(1f, 0.9f, 0.5f), TextAlignmentOptions.MidlineLeft); Stretch(result, 0f, 0.05f, 0.37f, 0.11f, 8f, 2f, 4f, 2f); }
        if (cool   != null) { StyleTMP(cool,   11, FontStyles.Normal, new Color(0.6f, 0.6f, 0.6f), TextAlignmentOptions.MidlineLeft); Stretch(cool,   0f, 0.01f, 0.37f, 0.05f, 8f, 1f, 4f, 1f); }

        // ── Centre — Turret slider ──────────────────────────────────────────────
        var turret = GO("PlayingPanel/TurretSlider");
        if (turret != null) Stretch(turret, 0.37f, 0.10f, 0.62f, 0.89f, 4f, 4f, 4f, 4f);

        // ── Right — HP panel ────────────────────────────────────────────────────
        var hpPanel = GO("PlayingPanel/RobotHpPanel");
        if (hpPanel != null)
        {
            BG(hpPanel, 0.04f, 0.04f, 0.07f, 0.95f);
            Stretch(hpPanel, 0.62f, 0.10f, 1f, 0.89f, 4f, 4f, 8f, 4f);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    //  ENDED
    // ═══════════════════════════════════════════════════════════════════════════════
    static void LayoutEnded(GameObject panel, GameObject backBtn, GameObject resultLabel)
    {
        if (panel == null) return;
        BG(panel, 0.06f, 0.06f, 0.10f);

        if (resultLabel != null)
        {
            StyleTMP(resultLabel, 46, FontStyles.Bold, new Color(1f, 0.85f, 0.1f));
            resultLabel.GetComponent<TextMeshProUGUI>().text = "Game Over";
            Stretch(resultLabel, 0.05f, 0.45f, 0.95f, 0.82f);
        }

        if (backBtn != null)
        {
            ButtonStyle(backBtn, new Color(0.18f, 0.45f, 0.72f), "Back to Menu", 20);
            Fixed(backBtn, 0.5f, 0.32f, 0.5f, 0.5f, 0f, 0f, 220f, 60f);
        }
    }
}
