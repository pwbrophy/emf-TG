using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lobby settings panel — builds its own rows at Awake.
/// Shows lobby-only settings (duration, victory points, countdown) plus
/// all live settings (damage, multipliers, cooldown, buzzer, slow turret speed).
/// </summary>
public class GameSettingsPanel : MonoBehaviour
{
    // Lobby-only fields
    private TMP_InputField _maxTeamPointsField;
    private TMP_InputField _durationField;
    private TMP_InputField _countdownField;

    // Live settings (also shown on PlayingSettingsPanel)
    private TMP_InputField _damageField;
    private TMP_InputField _sideMultField;
    private TMP_InputField _rearMultField;
    private TMP_InputField _killPointsField;
    private TMP_InputField _cooldownField;
    private TMP_InputField _explosionDurationField;
    private TMP_InputField _invulnField;
    private Toggle         _buzzerToggle;
    private TMP_InputField _slowTurretField;

    // Video controls
    private Button          _videoResButton;
    private TextMeshProUGUI _videoResLabel;
    private TMP_InputField  _videoFpsField;
    private TMP_InputField  _videoQualityField;

    // Resolution index → operator-facing name (matches firmware videoIdxToFrameSize)
    private static readonly string[] ResNames =
        { "QVGA 320x240", "CIF 400x296", "HVGA 480x320", "VGA 640x480" };

    private GameSettings _settings;
    private TMP_FontAsset _font;

    private void Awake()
    {
        _font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        BuildRows();
    }

    private void OnEnable()
    {
        _settings = ServiceLocator.GameSettings;
        if (_settings == null) return;
        Populate();
        Subscribe();
    }

    private void OnDisable() => Unsubscribe();

    // ── Build ────────────────────────────────────────────────────────────────

    void BuildRows()
    {
        // DestroyImmediate so the VLG never sees stale scene children alongside new rows.
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);

        var vlg = GetComponent<VerticalLayoutGroup>();
        if (vlg == null) vlg = gameObject.AddComponent<VerticalLayoutGroup>();
        // childControlHeight = false: VLG uses each child's existing sizeDelta.y (set explicitly
        // below) instead of trying to recalculate heights, which can fight inner HLG constraints.
        vlg.childControlHeight     = false;
        vlg.childControlWidth      = true;
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth  = true;
        vlg.spacing  = 3f;
        vlg.padding  = new RectOffset(6, 6, 4, 4);

        AddHeader("Game Settings");

        AddSectionLabel("── Lobby only ──");
        _maxTeamPointsField = AddInputRow("Victory Points:");
        _durationField      = AddInputRow("Duration (min):");
        _countdownField     = AddInputRow("Countdown (s):");

        AddSectionLabel("── Live settings ──");
        _damageField      = AddInputRow("Damage/Hit:");
        _sideMultField    = AddInputRow("Side Multi:");
        _rearMultField    = AddInputRow("Rear Multi:");
        _killPointsField  = AddInputRow("Kill Points:");
        _cooldownField    = AddInputRow("Cooldown (s):");
        _explosionDurationField = AddInputRow("Dead Time (s):");
        _invulnField      = AddInputRow("Invuln (s):");
        _buzzerToggle     = AddToggleRow("Buzzer SFX");
        _slowTurretField  = AddInputRow("Slow Turret:");

        AddSectionLabel("── Video ──");
        var resRow = AddButtonRow("Resolution:");
        _videoResButton    = resRow.btn;
        _videoResLabel     = resRow.label;
        _videoFpsField     = AddInputRow("Frame Rate:");
        _videoQualityField = AddInputRow("JPEG Quality:");
    }

    // ── Populate from GameSettings ───────────────────────────────────────────

    void Populate()
    {
        if (_maxTeamPointsField) _maxTeamPointsField.SetTextWithoutNotify(_settings.MaxTeamPoints.ToString());
        if (_durationField)      _durationField.SetTextWithoutNotify((_settings.MatchDurationSeconds / 60f).ToString("F0"));
        if (_countdownField)     _countdownField.SetTextWithoutNotify(_settings.CountdownDuration.ToString());
        if (_damageField)        _damageField.SetTextWithoutNotify(_settings.DamagePerHit.ToString());
        if (_sideMultField)      _sideMultField.SetTextWithoutNotify(_settings.SideMultiplier.ToString("F1"));
        if (_rearMultField)      _rearMultField.SetTextWithoutNotify(_settings.RearMultiplier.ToString("F1"));
        if (_killPointsField)    _killPointsField.SetTextWithoutNotify(_settings.TeamPointsPerKill.ToString());
        if (_cooldownField)      _cooldownField.SetTextWithoutNotify(_settings.FireCooldownSeconds.ToString("F1"));
        if (_explosionDurationField) _explosionDurationField.SetTextWithoutNotify(_settings.ExplosionDurationSeconds.ToString("F1"));
        if (_invulnField)        _invulnField.SetTextWithoutNotify(_settings.InvulnerabilitySeconds.ToString("F1"));
        if (_buzzerToggle)       _buzzerToggle.SetIsOnWithoutNotify(_settings.BuzzerEnabled);
        if (_slowTurretField)    _slowTurretField.SetTextWithoutNotify(_settings.SlowTurretSpeed.ToString("F2"));
        if (_videoResLabel)      _videoResLabel.text = ResName(_settings.VideoFrameSize);
        if (_videoFpsField)      _videoFpsField.SetTextWithoutNotify(_settings.VideoFps.ToString());
        if (_videoQualityField)  _videoQualityField.SetTextWithoutNotify(_settings.VideoJpegQuality.ToString());
    }

    static string ResName(int idx) => (idx >= 0 && idx < ResNames.Length) ? ResNames[idx] : ResNames[2];

    // ── Subscriptions ────────────────────────────────────────────────────────

    void Subscribe()
    {
        if (_maxTeamPointsField) _maxTeamPointsField.onValueChanged.AddListener(OnMaxTeamPoints);
        if (_durationField)      _durationField.onValueChanged.AddListener(OnDuration);
        if (_countdownField)     _countdownField.onValueChanged.AddListener(OnCountdown);
        if (_damageField)        _damageField.onValueChanged.AddListener(OnDamage);
        if (_sideMultField)      _sideMultField.onValueChanged.AddListener(OnSideMult);
        if (_rearMultField)      _rearMultField.onValueChanged.AddListener(OnRearMult);
        if (_killPointsField)    _killPointsField.onValueChanged.AddListener(OnKillPoints);
        if (_cooldownField)      _cooldownField.onValueChanged.AddListener(OnCooldown);
        if (_explosionDurationField) _explosionDurationField.onValueChanged.AddListener(OnExplosionDuration);
        if (_invulnField)        _invulnField.onValueChanged.AddListener(OnInvuln);
        if (_buzzerToggle)       _buzzerToggle.onValueChanged.AddListener(OnBuzzer);
        if (_slowTurretField)    _slowTurretField.onValueChanged.AddListener(OnSlowTurret);
        if (_videoResButton)     _videoResButton.onClick.AddListener(OnVideoResCycle);
        if (_videoFpsField)      _videoFpsField.onValueChanged.AddListener(OnVideoFps);
        if (_videoQualityField)  _videoQualityField.onValueChanged.AddListener(OnVideoQuality);
    }

    void Unsubscribe()
    {
        if (_maxTeamPointsField) _maxTeamPointsField.onValueChanged.RemoveListener(OnMaxTeamPoints);
        if (_durationField)      _durationField.onValueChanged.RemoveListener(OnDuration);
        if (_countdownField)     _countdownField.onValueChanged.RemoveListener(OnCountdown);
        if (_damageField)        _damageField.onValueChanged.RemoveListener(OnDamage);
        if (_sideMultField)      _sideMultField.onValueChanged.RemoveListener(OnSideMult);
        if (_rearMultField)      _rearMultField.onValueChanged.RemoveListener(OnRearMult);
        if (_killPointsField)    _killPointsField.onValueChanged.RemoveListener(OnKillPoints);
        if (_cooldownField)      _cooldownField.onValueChanged.RemoveListener(OnCooldown);
        if (_explosionDurationField) _explosionDurationField.onValueChanged.RemoveListener(OnExplosionDuration);
        if (_invulnField)        _invulnField.onValueChanged.RemoveListener(OnInvuln);
        if (_buzzerToggle)       _buzzerToggle.onValueChanged.RemoveListener(OnBuzzer);
        if (_slowTurretField)    _slowTurretField.onValueChanged.RemoveListener(OnSlowTurret);
        if (_videoResButton)     _videoResButton.onClick.RemoveListener(OnVideoResCycle);
        if (_videoFpsField)      _videoFpsField.onValueChanged.RemoveListener(OnVideoFps);
        if (_videoQualityField)  _videoQualityField.onValueChanged.RemoveListener(OnVideoQuality);
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    void OnMaxTeamPoints(string v) { if (_settings != null && int.TryParse(v, out int n) && n > 0) { _settings.MaxTeamPoints = n; _settings.SaveToDisk(); } }
    void OnDuration(string v)      { if (_settings != null && float.TryParse(v, out float n) && n > 0) { _settings.MatchDurationSeconds = n * 60f; _settings.SaveToDisk(); } }
    void OnCountdown(string v)     { if (_settings != null && int.TryParse(v, out int n) && n >= 1 && n <= 60) { _settings.CountdownDuration = n; _settings.SaveToDisk(); } }
    void OnDamage(string v)        { if (_settings != null && int.TryParse(v, out int n) && n > 0) { _settings.DamagePerHit = n; _settings.SaveToDisk(); } }
    void OnSideMult(string v)      { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.SideMultiplier = n; _settings.SaveToDisk(); } }
    void OnRearMult(string v)      { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.RearMultiplier = n; _settings.SaveToDisk(); } }
    void OnKillPoints(string v)    { if (_settings != null && int.TryParse(v, out int n) && n >= 0) { _settings.TeamPointsPerKill = n; _settings.SaveToDisk(); } }
    void OnCooldown(string v)      { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.FireCooldownSeconds = n; _settings.SaveToDisk(); } }
    void OnExplosionDuration(string v) { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.ExplosionDurationSeconds = n; _settings.SaveToDisk(); } }
    void OnInvuln(string v)        { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.InvulnerabilitySeconds = n; _settings.SaveToDisk(); } }
    void OnBuzzer(bool on)         { if (_settings != null) { _settings.BuzzerEnabled = on; _settings.SaveToDisk(); ServiceLocator.RobotServer?.BroadcastBuzzerToAll(on); } }
    void OnSlowTurret(string v)    { if (_settings != null && float.TryParse(v, out float n) && n > 0f && n <= 1f) { _settings.SlowTurretSpeed = n; _settings.SaveToDisk(); } }

    void OnVideoResCycle()
    {
        if (_settings == null) return;
        _settings.VideoFrameSize = (Mathf.Clamp(_settings.VideoFrameSize, 0, ResNames.Length - 1) + 1) % ResNames.Length;
        if (_videoResLabel) _videoResLabel.text = ResName(_settings.VideoFrameSize);
        _settings.SaveToDisk();
        ServiceLocator.RobotServer?.BroadcastVideoConfigToAll(_settings);
    }
    void OnVideoFps(string v)     { if (_settings != null && int.TryParse(v, out int n) && n >= 1 && n <= 30) { _settings.VideoFps = n; _settings.SaveToDisk(); ServiceLocator.RobotServer?.BroadcastVideoConfigToAll(_settings); } }
    void OnVideoQuality(string v) { if (_settings != null && int.TryParse(v, out int n) && n >= 8 && n <= 40) { _settings.VideoJpegQuality = n; _settings.SaveToDisk(); ServiceLocator.RobotServer?.BroadcastVideoConfigToAll(_settings); } }

    // ── Row builders ─────────────────────────────────────────────────────────

    void AddHeader(string text)
    {
        var go = new GameObject("Header");
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(transform, false);
        rt.sizeDelta = new Vector2(0, 28f);
        go.AddComponent<LayoutElement>().preferredHeight = 28f;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (_font) tmp.font = _font;
        tmp.text = text; tmp.fontSize = 14; tmp.fontStyle = FontStyles.Bold;
        tmp.color = new Color(0.9f, 0.75f, 0.2f);
        tmp.alignment = TextAlignmentOptions.Left;
    }

    void AddSectionLabel(string text)
    {
        var go = new GameObject("Section");
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(transform, false);
        rt.sizeDelta = new Vector2(0, 18f);
        go.AddComponent<LayoutElement>().preferredHeight = 18f;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (_font) tmp.font = _font;
        tmp.text = text; tmp.fontSize = 10;
        tmp.color = new Color(0.5f, 0.5f, 0.5f);
        tmp.alignment = TextAlignmentOptions.Left;
    }

    TMP_InputField AddInputRow(string label)
    {
        var row = new GameObject(label.Replace(":", "") + "Row");
        var rowRT = row.AddComponent<RectTransform>();
        row.transform.SetParent(transform, false);
        rowRT.sizeDelta = new Vector2(0, 26f);
        row.AddComponent<LayoutElement>().preferredHeight = 26f;
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.childControlHeight = true; hlg.childControlWidth = true;
        hlg.childForceExpandHeight = true; hlg.childForceExpandWidth = false;
        hlg.spacing = 4f;

        var lgo = new GameObject("Lbl");
        var lLE = lgo.AddComponent<LayoutElement>(); lLE.preferredWidth = 95f; lLE.flexibleWidth = 0;
        lgo.transform.SetParent(row.transform, false);
        var ltmp = lgo.AddComponent<TextMeshProUGUI>();
        if (_font) ltmp.font = _font;
        ltmp.text = label; ltmp.fontSize = 12; ltmp.color = Color.white;
        ltmp.alignment = TextAlignmentOptions.Right;

        // Create igo inactive so TMP_InputField.OnEnable fires only after textViewport
        // and textComponent are both set — otherwise the dirty-vertex callbacks that
        // keep the label live never get registered, making the field appear non-editable
        // on the first lobby entry.
        var igo = new GameObject("Field");
        igo.SetActive(false);
        var iLE = igo.AddComponent<LayoutElement>(); iLE.preferredWidth = 70f; iLE.flexibleWidth = 1;
        igo.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.2f);
        var field = igo.AddComponent<TMP_InputField>();
        field.contentType = TMP_InputField.ContentType.DecimalNumber;

        var taGO = new GameObject("Text Area");
        var taRT = taGO.AddComponent<RectTransform>();
        taGO.transform.SetParent(igo.transform, false);
        taGO.AddComponent<RectMask2D>();
        taRT.anchorMin = Vector2.zero; taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(4, 2); taRT.offsetMax = new Vector2(-4, -2);
        field.textViewport = taRT;

        var tgo = new GameObject("Text");
        tgo.transform.SetParent(taGO.transform, false);
        var trt = tgo.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        var ttmp = tgo.AddComponent<TextMeshProUGUI>();
        if (_font) ttmp.font = _font;
        ttmp.fontSize = 12; ttmp.color = Color.white;
        ttmp.alignment = TextAlignmentOptions.Left;
        field.textComponent = ttmp;

        igo.transform.SetParent(row.transform, false);
        igo.SetActive(true);

        return field;
    }

    (Button btn, TextMeshProUGUI label) AddButtonRow(string label)
    {
        var row = new GameObject(label.Replace(":", "") + "Row");
        var rowRT = row.AddComponent<RectTransform>();
        row.transform.SetParent(transform, false);
        rowRT.sizeDelta = new Vector2(0, 26f);
        row.AddComponent<LayoutElement>().preferredHeight = 26f;
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.childControlHeight = true; hlg.childControlWidth = true;
        hlg.childForceExpandHeight = true; hlg.childForceExpandWidth = false;
        hlg.spacing = 4f;

        var lgo = new GameObject("Lbl");
        var lLE = lgo.AddComponent<LayoutElement>(); lLE.preferredWidth = 95f; lLE.flexibleWidth = 0;
        lgo.transform.SetParent(row.transform, false);
        var ltmp = lgo.AddComponent<TextMeshProUGUI>();
        if (_font) ltmp.font = _font;
        ltmp.text = label; ltmp.fontSize = 12; ltmp.color = Color.white;
        ltmp.alignment = TextAlignmentOptions.Right;

        var bgo = new GameObject("Btn");
        var bLE = bgo.AddComponent<LayoutElement>(); bLE.preferredWidth = 70f; bLE.flexibleWidth = 1;
        bgo.transform.SetParent(row.transform, false);
        var bimg = bgo.AddComponent<Image>();
        bimg.color = new Color(0.18f, 0.18f, 0.24f);
        var btn = bgo.AddComponent<Button>();
        btn.targetGraphic = bimg;

        var tgo = new GameObject("Text");
        tgo.transform.SetParent(bgo.transform, false);
        var trt = tgo.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(4, 2); trt.offsetMax = new Vector2(-4, -2);
        var ttmp = tgo.AddComponent<TextMeshProUGUI>();
        if (_font) ttmp.font = _font;
        ttmp.fontSize = 12; ttmp.color = Color.white;
        ttmp.alignment = TextAlignmentOptions.Center;

        return (btn, ttmp);
    }

    Toggle AddToggleRow(string label)
    {
        var row = new GameObject(label + "Row");
        var rowRT = row.AddComponent<RectTransform>();
        row.transform.SetParent(transform, false);
        rowRT.sizeDelta = new Vector2(0, 26f);
        row.AddComponent<LayoutElement>().preferredHeight = 26f;
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        // childControlWidth = true, and Lbl pinned to the same 95px width as every
        // AddInputRow label, so the checkbox lands in the same value column as the
        // other rows' input fields instead of trailing the (variable-width) text
        // by an inconsistent gap or drifting off-screen in a narrow column.
        hlg.childControlHeight = true; hlg.childControlWidth = true;
        hlg.childForceExpandHeight = true; hlg.childForceExpandWidth = false;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.spacing = 4f;

        var lgo = new GameObject("Lbl");
        lgo.transform.SetParent(row.transform, false);
        var lLE = lgo.AddComponent<LayoutElement>();
        lLE.preferredWidth = 95f; lLE.flexibleWidth = 0f;
        var ltmp = lgo.AddComponent<TextMeshProUGUI>();
        if (_font) ltmp.font = _font;
        ltmp.text = label; ltmp.fontSize = 12; ltmp.color = Color.white;
        ltmp.alignment = TextAlignmentOptions.Right;

        var tgo = new GameObject("Toggle");
        var trt = tgo.AddComponent<RectTransform>();
        tgo.transform.SetParent(row.transform, false);
        var tLE = tgo.AddComponent<LayoutElement>();
        tLE.preferredWidth = tLE.preferredHeight = 20f; tLE.flexibleWidth = 0f;
        tgo.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.3f);
        var tog = tgo.AddComponent<Toggle>();

        var check = new GameObject("Check");
        var crt = check.AddComponent<RectTransform>();
        check.transform.SetParent(tgo.transform, false);
        crt.anchorMin = new Vector2(0.1f, 0.1f); crt.anchorMax = new Vector2(0.9f, 0.9f);
        crt.offsetMin = crt.offsetMax = Vector2.zero;
        var cimg = check.AddComponent<Image>();
        cimg.color = new Color(0.2f, 0.8f, 0.3f);
        tog.graphic = cimg;
        tog.targetGraphic = tgo.GetComponent<Image>();

        return tog;
    }
}
