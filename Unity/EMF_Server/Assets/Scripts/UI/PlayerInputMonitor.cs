// PlayerInputMonitor.cs
// Shows a live table of each phone player's drive and turret inputs in the PlayingPanel.
// Subscribe to PlayerWebSocketServer.OnPlayerInput and update labels each frame.

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PlayerInputMonitor : MonoBehaviour
{
    [SerializeField] private RectTransform rowContainer;   // VLG + ContentSizeFitter

    private PlayerWebSocketServer _server;
    private PlayersService        _players;

    private struct InputState
    {
        public float Left;
        public float Right;
        public float Turret;
    }

    private readonly Dictionary<string, InputState>      _inputs  = new Dictionary<string, InputState>();
    private readonly Dictionary<string, TextMeshProUGUI> _labels  = new Dictionary<string, TextMeshProUGUI>();

    private bool _dirty = true;   // true when rows need to be rebuilt

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    void OnEnable()
    {
        _server  = ServiceLocator.PlayerServer;
        _players = ServiceLocator.Players;

        if (_server  != null) _server.OnPlayerInput              += OnInput;
        if (_players != null) _players.OnChanged                 += OnPlayersChanged;

        _dirty = true;
    }

    void OnDisable()
    {
        if (_server  != null) _server.OnPlayerInput  -= OnInput;
        if (_players != null) _players.OnChanged     -= OnPlayersChanged;
    }

    // ── Events ───────────────────────────────────────────────────────────────────

    void OnInput(string playerName, float l, float r, float turret)
    {
        _inputs[playerName] = new InputState { Left = l, Right = r, Turret = turret };
    }

    void OnPlayersChanged() => _dirty = true;

    // ── Update ───────────────────────────────────────────────────────────────────

    void Update()
    {
        if (_dirty) RebuildRows();

        // Refresh label text for every known player
        if (_players == null) return;
        var all = _players.GetAll();
        foreach (var p in all)
        {
            if (!_labels.TryGetValue(p.Name, out var lbl)) continue;

            _inputs.TryGetValue(p.Name, out var inp);

            lbl.text = string.Format(
                "<b>{0}</b>  L:{1:+0.00;-0.00;0.00}  R:{2:+0.00;-0.00;0.00}  T:{3:+0.00;-0.00;0.00}",
                p.Name, inp.Left, inp.Right, inp.Turret);
        }
    }

    // ── Row management ───────────────────────────────────────────────────────────

    void RebuildRows()
    {
        _dirty = false;
        if (rowContainer == null) return;

        // Destroy existing rows
        foreach (Transform child in rowContainer)
            Destroy(child.gameObject);
        _labels.Clear();

        if (_players == null) return;

        var font = GetTmpFont();
        var all  = _players.GetAll();

        for (int i = 0; i < all.Count; i++)
        {
            string name = all[i].Name;
            var go = CreateRow(i, font);
            if (go == null) continue;

            var lbl = go.GetComponentInChildren<TextMeshProUGUI>();
            if (lbl != null) _labels[name] = lbl;
        }
    }

    GameObject CreateRow(int index, TMP_FontAsset font)
    {
        // Row background
        var go = new GameObject("InputRow_" + index);
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(rowContainer, false);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 30f;
        le.flexibleWidth   = 1f;

        var img = go.AddComponent<Image>();
        img.color = index % 2 == 0
            ? new Color(0.10f, 0.10f, 0.16f)
            : new Color(0.07f, 0.07f, 0.12f);

        // Label
        var lblGo = new GameObject("Label");
        var lblRt = lblGo.AddComponent<RectTransform>();
        lblGo.transform.SetParent(go.transform, false);
        lblRt.anchorMin = Vector2.zero;
        lblRt.anchorMax = Vector2.one;
        lblRt.offsetMin = new Vector2(8f, 2f);
        lblRt.offsetMax = new Vector2(-8f, -2f);

        var tmp = lblGo.AddComponent<TextMeshProUGUI>();
        tmp.fontSize    = 13f;
        tmp.color       = new Color(0.8f, 0.8f, 0.9f);
        tmp.alignment   = TextAlignmentOptions.MidlineLeft;
        tmp.richText    = true;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        if (font != null) tmp.font = font;

        return go;
    }

    static TMP_FontAsset GetTmpFont()
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
#else
        return null;
#endif
    }
}
