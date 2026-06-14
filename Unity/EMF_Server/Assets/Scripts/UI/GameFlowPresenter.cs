using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class GameFlowPresenter : MonoBehaviour
{
    [Header("Top-level screens/panels")]
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private GameObject playingPanel;
    [SerializeField] private GameObject endedPanel;

    [Header("Buttons")]
    [SerializeField] private Button toLobbyButton;
    [SerializeField] private Button startGameButton;
    [SerializeField] private Button backToMenuButton;
    [SerializeField] private Button endGameButton;

    private GameFlow _flow;

    // Confirm dialog built at runtime
    private GameObject      _confirmOverlay;
    private TextMeshProUGUI _confirmMsgText;

    void Start()
    {
        BuildConfirmDialog();
    }

    void OnEnable()
    {
        _flow = ServiceLocator.GameFlow;
        if (_flow == null)
        {
            Debug.LogError("[GameFlowPresenter] GameFlow is null — AppBootstrap may not have run yet.");
            return;
        }

        if (toLobbyButton)    toLobbyButton.onClick.AddListener(() => _flow?.GoToLobby());
        if (startGameButton)  startGameButton.onClick.AddListener(OnStartGameClicked);
        if (backToMenuButton) backToMenuButton.onClick.AddListener(() => _flow?.GoToLobby());
        if (endGameButton)    endGameButton.onClick.AddListener(() => _flow?.EndGame());

        _flow.OnPhaseChanged += HandlePhaseChanged;

        HandlePhaseChanged(_flow.Phase);
    }

    void OnDisable()
    {
        if (_flow != null) _flow.OnPhaseChanged -= HandlePhaseChanged;

        if (toLobbyButton)    toLobbyButton.onClick.RemoveAllListeners();
        if (startGameButton)  startGameButton.onClick.RemoveAllListeners();
        if (backToMenuButton) backToMenuButton.onClick.RemoveAllListeners();
        if (endGameButton)    endGameButton.onClick.RemoveAllListeners();
    }

    void HandlePhaseChanged(GamePhase p)
    {
        if (mainMenuPanel) mainMenuPanel.SetActive(p == GamePhase.MainMenu);
        if (lobbyPanel)    lobbyPanel.SetActive(p == GamePhase.Lobby);
        if (playingPanel)  playingPanel.SetActive(p == GamePhase.Playing);
        if (endedPanel)    endedPanel.SetActive(p == GamePhase.Ended);

        if (startGameButton) startGameButton.interactable = _flow.CanStartGame();

        // Hide the confirm dialog whenever the phase changes (e.g. game already ended)
        if (_confirmOverlay != null) _confirmOverlay.SetActive(false);
    }

    // ── Start-game flow ──────────────────────────────────────────────────────────

    void OnStartGameClicked()
    {
        if (_flow == null || !_flow.CanStartGame()) return;

        var players = ServiceLocator.Players?.GetAll();
        var dir     = ServiceLocator.RobotDirectory;

        // Collect players without a robot assignment
        var unassigned = new List<string>();
        if (players != null && dir != null)
        {
            foreach (var p in players)
            {
                bool hasRobot = false;
                foreach (var r in dir.GetAll())
                    if (r.AssignedPlayer == p.Name || r.GunnerPlayer == p.Name) { hasRobot = true; break; }
                if (!hasRobot) unassigned.Add(p.Name);
            }
        }

        if (unassigned.Count == 0)
        {
            ServiceLocator.Countdown?.TriggerStart(false);
            return;
        }

        // Show confirmation dialog
        if (_confirmMsgText != null)
        {
            string names = string.Join(", ", unassigned);
            _confirmMsgText.text =
                "These players are not assigned a tank:\n<b>" + names + "</b>\n\n" +
                "If you continue, they will be returned to the join screen.";
        }

        if (_confirmOverlay != null)
        {
            _confirmOverlay.transform.SetAsLastSibling();
            _confirmOverlay.SetActive(true);
        }
    }

    void StartGameWithKick()
    {
        if (_confirmOverlay != null) _confirmOverlay.SetActive(false);
        ServiceLocator.Countdown?.TriggerStart(true);
    }

    // ── Confirm dialog builder ───────────────────────────────────────────────────

    void BuildConfirmDialog()
    {
        if (_confirmOverlay != null) return;

        var canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("[GameFlowPresenter] Cannot build confirm dialog: no Canvas found.");
            return;
        }

        var font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");

        // ── Full-screen overlay (blocks input) ───────────────────────────────────
        _confirmOverlay = new GameObject("StartGameConfirmOverlay");
        var ovRT = _confirmOverlay.AddComponent<RectTransform>();
        _confirmOverlay.transform.SetParent(canvas.transform, false);
        ovRT.anchorMin = Vector2.zero;
        ovRT.anchorMax = Vector2.one;
        ovRT.offsetMin = ovRT.offsetMax = Vector2.zero;
        var ovImg = _confirmOverlay.AddComponent<Image>();
        ovImg.color = new Color(0f, 0f, 0f, 0.65f);
        _confirmOverlay.SetActive(false);

        // ── Dialog box ───────────────────────────────────────────────────────────
        // Height = 18 (top pad) + 120 (msg) + 12 (spacing) + 46 (buttons) + 18 (bottom pad) = 214
        var box    = new GameObject("ConfirmDialogBox");
        var boxRT  = box.AddComponent<RectTransform>();
        box.transform.SetParent(_confirmOverlay.transform, false);
        boxRT.anchorMin = boxRT.anchorMax = boxRT.pivot = new Vector2(0.5f, 0.5f);
        boxRT.sizeDelta = new Vector2(480, 214);
        var boxImg = box.AddComponent<Image>();
        boxImg.color = new Color(0.11f, 0.11f, 0.18f, 1f);

        var vlg = box.AddComponent<VerticalLayoutGroup>();
        vlg.padding             = new RectOffset(20, 20, 18, 18);
        vlg.spacing             = 12;
        vlg.childControlHeight  = false;
        vlg.childControlWidth   = true;
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth  = true;

        // Message text
        var msgGo = new GameObject("ConfirmMsg");
        var msgRT = msgGo.AddComponent<RectTransform>();
        msgGo.transform.SetParent(box.transform, false);
        msgRT.sizeDelta = new Vector2(0, 120);
        _confirmMsgText = msgGo.AddComponent<TextMeshProUGUI>();
        if (font != null) _confirmMsgText.font = font;
        _confirmMsgText.fontSize         = 15;
        _confirmMsgText.color            = Color.white;
        _confirmMsgText.enableWordWrapping = true;
        _confirmMsgText.alignment        = TextAlignmentOptions.TopLeft;

        // Button row
        var btnRow   = new GameObject("BtnRow");
        var btnRowRT = btnRow.AddComponent<RectTransform>();
        btnRow.transform.SetParent(box.transform, false);
        btnRowRT.sizeDelta = new Vector2(0, 46);
        var hlg = btnRow.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing              = 12;
        hlg.childControlWidth    = true;
        hlg.childControlHeight   = true;
        hlg.childForceExpandWidth  = true;
        hlg.childForceExpandHeight = true;

        var cancelBtn = CreateDialogButton(btnRow.transform, font, "Cancel",
                                           new Color(0.45f, 0.12f, 0.12f));
        cancelBtn.onClick.AddListener(() => _confirmOverlay.SetActive(false));

        var continueBtn = CreateDialogButton(btnRow.transform, font, "Continue",
                                             new Color(0.12f, 0.40f, 0.12f));
        continueBtn.onClick.AddListener(StartGameWithKick);
    }

    static Button CreateDialogButton(Transform parent, TMP_FontAsset font,
                                     string label, Color bgColor)
    {
        var go = new GameObject(label + "Btn");
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = bgColor;
        var btn = go.AddComponent<Button>();

        var txtGo = new GameObject("Label");
        var txtRT = txtGo.AddComponent<RectTransform>();
        txtGo.transform.SetParent(go.transform, false);
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = txtRT.offsetMax = Vector2.zero;

        var txt = txtGo.AddComponent<TextMeshProUGUI>();
        if (font != null) txt.font = font;
        txt.text      = label;
        txt.fontSize  = 15;
        txt.fontStyle = FontStyles.Bold;
        txt.color     = Color.white;
        txt.alignment = TextAlignmentOptions.Center;

        return btn;
    }
}
