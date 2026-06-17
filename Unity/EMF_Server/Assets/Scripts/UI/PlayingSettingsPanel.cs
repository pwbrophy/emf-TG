using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PlayingSettingsPanel : MonoBehaviour
{
    private TMP_InputField _damageField;
    private TMP_InputField _sideMultField;
    private TMP_InputField _rearMultField;
    private TMP_InputField _killPointsField;
    private TMP_InputField _cooldownField;
    private Toggle         _buzzerToggle;
    private Toggle         _slowTurretToggle;

    private GameSettings  _settings;
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

    void BuildRows()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);

        var vlg = GetComponent<VerticalLayoutGroup>();
        if (vlg == null) vlg = gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.childControlHeight    = false;
        vlg.childControlWidth     = true;
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth  = true;
        vlg.spacing  = 3f;
        vlg.padding  = new RectOffset(6, 6, 4, 4);

        AddHeader("Live Settings");
        _damageField      = AddInputRow("Damage/Hit:");
        _sideMultField    = AddInputRow("Side Multi:");
        _rearMultField    = AddInputRow("Rear Multi:");
        _killPointsField  = AddInputRow("Kill Points:");
        _cooldownField    = AddInputRow("Cooldown (s):");
        _buzzerToggle     = AddToggleRow("Buzzer SFX");
        _slowTurretToggle = AddToggleRow("Slow Turret");
    }

    void Populate()
    {
        if (_damageField)      _damageField.SetTextWithoutNotify(_settings.DamagePerHit.ToString());
        if (_sideMultField)    _sideMultField.SetTextWithoutNotify(_settings.SideMultiplier.ToString("F1"));
        if (_rearMultField)    _rearMultField.SetTextWithoutNotify(_settings.RearMultiplier.ToString("F1"));
        if (_killPointsField)  _killPointsField.SetTextWithoutNotify(_settings.TeamPointsPerKill.ToString());
        if (_cooldownField)    _cooldownField.SetTextWithoutNotify(_settings.FireCooldownSeconds.ToString("F1"));
        if (_buzzerToggle)     _buzzerToggle.SetIsOnWithoutNotify(_settings.BuzzerEnabled);
        if (_slowTurretToggle) _slowTurretToggle.SetIsOnWithoutNotify(_settings.SlowTurretEnabled);
    }

    void Subscribe()
    {
        if (_damageField)      _damageField.onValueChanged.AddListener(OnDamage);
        if (_sideMultField)    _sideMultField.onValueChanged.AddListener(OnSideMult);
        if (_rearMultField)    _rearMultField.onValueChanged.AddListener(OnRearMult);
        if (_killPointsField)  _killPointsField.onValueChanged.AddListener(OnKillPoints);
        if (_cooldownField)    _cooldownField.onValueChanged.AddListener(OnCooldown);
        if (_buzzerToggle)     _buzzerToggle.onValueChanged.AddListener(OnBuzzer);
        if (_slowTurretToggle) _slowTurretToggle.onValueChanged.AddListener(OnSlowTurret);
    }

    void Unsubscribe()
    {
        if (_damageField)      _damageField.onValueChanged.RemoveListener(OnDamage);
        if (_sideMultField)    _sideMultField.onValueChanged.RemoveListener(OnSideMult);
        if (_rearMultField)    _rearMultField.onValueChanged.RemoveListener(OnRearMult);
        if (_killPointsField)  _killPointsField.onValueChanged.RemoveListener(OnKillPoints);
        if (_cooldownField)    _cooldownField.onValueChanged.RemoveListener(OnCooldown);
        if (_buzzerToggle)     _buzzerToggle.onValueChanged.RemoveListener(OnBuzzer);
        if (_slowTurretToggle) _slowTurretToggle.onValueChanged.RemoveListener(OnSlowTurret);
    }

    void OnDamage(string v)    { if (_settings != null && int.TryParse(v, out int n) && n > 0) { _settings.DamagePerHit = n; _settings.SaveToDisk(); } }
    void OnSideMult(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.SideMultiplier = n; _settings.SaveToDisk(); } }
    void OnRearMult(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.RearMultiplier = n; _settings.SaveToDisk(); } }
    void OnKillPoints(string v){ if (_settings != null && int.TryParse(v, out int n) && n >= 0) { _settings.TeamPointsPerKill = n; _settings.SaveToDisk(); } }
    void OnCooldown(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.FireCooldownSeconds = n; _settings.SaveToDisk(); } }
    void OnBuzzer(bool on)     { if (_settings != null) { _settings.BuzzerEnabled = on; _settings.SaveToDisk(); ServiceLocator.RobotServer?.BroadcastBuzzerToAll(on); } }
    void OnSlowTurret(bool on) { if (_settings != null) { _settings.SlowTurretEnabled = on; _settings.SaveToDisk(); } }

    void AddHeader(string text)
    {
        var go = new GameObject("Header");
        go.AddComponent<LayoutElement>().preferredHeight = 28f;
        go.transform.SetParent(transform, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (_font) tmp.font = _font;
        tmp.text = text; tmp.fontSize = 14; tmp.fontStyle = FontStyles.Bold;
        tmp.color = new Color(0.9f, 0.75f, 0.2f);
        tmp.alignment = TextAlignmentOptions.Left;
    }

    TMP_InputField AddInputRow(string label)
    {
        var row = new GameObject(label.Replace(":", "") + "Row");
        row.AddComponent<LayoutElement>().preferredHeight = 26f;
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.childControlHeight = true; hlg.childControlWidth = true;
        hlg.childForceExpandHeight = true; hlg.childForceExpandWidth = false;
        hlg.spacing = 4f;
        row.transform.SetParent(transform, false);

        var lgo = new GameObject("Lbl");
        var lLE = lgo.AddComponent<LayoutElement>(); lLE.preferredWidth = 90f; lLE.flexibleWidth = 0;
        lgo.transform.SetParent(row.transform, false);
        var ltmp = lgo.AddComponent<TextMeshProUGUI>();
        if (_font) ltmp.font = _font;
        ltmp.text = label; ltmp.fontSize = 12; ltmp.color = Color.white;
        ltmp.alignment = TextAlignmentOptions.Right;

        var igo = new GameObject("Field");
        var iLE = igo.AddComponent<LayoutElement>(); iLE.preferredWidth = 60f; iLE.flexibleWidth = 1;
        igo.transform.SetParent(row.transform, false);
        igo.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.2f);
        var field = igo.AddComponent<TMP_InputField>();
        field.contentType = TMP_InputField.ContentType.DecimalNumber;

        var tgo = new GameObject("Text");
        tgo.transform.SetParent(igo.transform, false);
        var trt = tgo.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(4, 2); trt.offsetMax = new Vector2(-4, -2);
        var ttmp = tgo.AddComponent<TextMeshProUGUI>();
        if (_font) ttmp.font = _font;
        ttmp.fontSize = 12; ttmp.color = Color.white;
        ttmp.alignment = TextAlignmentOptions.Left;
        field.textComponent = ttmp;

        return field;
    }

    Toggle AddToggleRow(string label)
    {
        var row = new GameObject(label + "Row");
        row.AddComponent<LayoutElement>().preferredHeight = 26f;
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.childControlHeight = true; hlg.childControlWidth = false;
        hlg.childForceExpandHeight = true;
        hlg.spacing = 6f; hlg.padding = new RectOffset(8, 0, 0, 0);
        row.transform.SetParent(transform, false);

        var tgo = new GameObject("Toggle");
        tgo.AddComponent<RectTransform>().sizeDelta = new Vector2(20, 20);
        tgo.transform.SetParent(row.transform, false);
        tgo.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.3f);
        var tog = tgo.AddComponent<Toggle>();

        var check = new GameObject("Check");
        var crt = check.AddComponent<RectTransform>();
        check.transform.SetParent(tgo.transform, false);
        crt.anchorMin = new Vector2(0.1f, 0.1f); crt.anchorMax = new Vector2(0.9f, 0.9f);
        crt.offsetMin = crt.offsetMax = Vector2.zero;
        var cimg = check.AddComponent<Image>(); cimg.color = new Color(0.2f, 0.8f, 0.3f);
        tog.graphic = cimg; tog.targetGraphic = tgo.GetComponent<Image>();

        var lgo = new GameObject("Lbl");
        lgo.transform.SetParent(row.transform, false);
        var ltmp = lgo.AddComponent<TextMeshProUGUI>();
        if (_font) ltmp.font = _font;
        ltmp.text = label; ltmp.fontSize = 12; ltmp.color = Color.white;
        ltmp.alignment = TextAlignmentOptions.Left;

        return tog;
    }
}
