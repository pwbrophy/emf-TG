// BeaconsPanelPresenter.cs
// Shows the 3 fixed capture-point beacons (North/Centre/South) in the Lobby,
// stacked under the robots list. Unlike RobotsPanelPresenter, the row count
// never changes — there's always exactly one row per capture point, and rows
// are built at runtime (no prefab) since there's no edit/assign UI needed.
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class BeaconsPanelPresenter : MonoBehaviour
{
    [SerializeField] private RectTransform content;

    private CapturePointBeaconDirectory _dir;
    private bool _isSubscribed = false;

    private static TMP_FontAsset _font;

    private void OnEnable()
    {
        _dir = ServiceLocator.BeaconDirectory;

        if (_dir == null)
        {
            Debug.LogError("[BeaconsPanelPresenter] BeaconDirectory is null. Is AppBootstrap in the scene and enabled?");
            return;
        }

        if (content == null)
        {
            Debug.LogError("[BeaconsPanelPresenter] 'content' is not assigned.");
            return;
        }

        RebuildFromDirectory();

        if (!_isSubscribed)
        {
            _dir.OnBeaconUpdated += HandleBeaconUpdated;
            _isSubscribed = true;
        }
    }

    private void OnDisable()
    {
        if (_dir != null && _isSubscribed)
        {
            _dir.OnBeaconUpdated -= HandleBeaconUpdated;
            _isSubscribed = false;
        }
    }

    private void HandleBeaconUpdated(int pointIndex) => RebuildFromDirectory();

    private void RebuildFromDirectory()
    {
        for (int i = content.childCount - 1; i >= 0; i--)
            Destroy(content.GetChild(i).gameObject);

        foreach (var b in _dir.GetAll())
            CreateRow(b);
    }

    private void CreateRow(BeaconInfo b)
    {
        var rowGO = new GameObject(b.PointName);
        var rowRT = rowGO.AddComponent<RectTransform>();
        rowGO.transform.SetParent(content, false);
        rowRT.sizeDelta = new Vector2(0f, 24f);

        var layout = rowGO.AddComponent<HorizontalLayoutGroup>();
        layout.childControlWidth  = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.spacing = 6f;

        MakeLabel(rowGO.transform, b.PointName, 0.45f, TextAlignmentOptions.Left, Color.white);
        MakeLabel(rowGO.transform, string.IsNullOrEmpty(b.Ip) ? "IP: ?" : "IP: " + b.Ip,
                  0.35f, TextAlignmentOptions.Left, new Color(0.75f, 0.75f, 0.75f));

        Color statusColor = b.Connected ? new Color(0.35f, 0.85f, 0.35f) : new Color(0.7f, 0.3f, 0.3f);
        MakeLabel(rowGO.transform, b.Connected ? "Connected" : "Offline",
                  0.20f, TextAlignmentOptions.Right, statusColor);
    }

    private void MakeLabel(Transform parent, string text, float widthFraction,
                            TextAlignmentOptions align, Color color)
    {
        var go = new GameObject("Label");
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);

        var le = go.AddComponent<LayoutElement>();
        le.flexibleWidth = widthFraction;

        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (_font == null) _font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        tmp.font = _font;
        tmp.fontSize = 14f;
        tmp.text = text;
        tmp.color = color;
        tmp.alignment = align;
    }
}
