using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tug-of-war team points bar. Desert Squad fills from the left, Jungle Squad from the right.
/// Subscribes to CapturePointService.OnTeamPointsChanged.
/// </summary>
public class TeamPointsBarUI : MonoBehaviour
{
    [SerializeField] public RectTransform fill0;   // Desert Squad (tan, left)
    [SerializeField] public RectTransform fill1;   // Jungle Squad (olive, right)
    [SerializeField] public TextMeshProUGUI label0;
    [SerializeField] public TextMeshProUGUI label1;

    static readonly Color C_A0 = new Color(1.000f, 0.835f, 0.580f);  // Desert Squad #FFD594
    static readonly Color C_A1 = new Color(0.200f, 0.549f, 0.184f);  // Jungle Squad #338C2F

    private CapturePointService _cp;

    private void Awake()
    {
        if (fill0 != null) { var img = fill0.GetComponent<Image>(); if (img != null) img.color = C_A0; }
        if (fill1 != null) { var img = fill1.GetComponent<Image>(); if (img != null) img.color = C_A1; }
        if (label0 != null) label0.color = C_A0;
        if (label1 != null) label1.color = C_A1;
    }

    private void OnEnable()
    {
        _cp = ServiceLocator.CapturePoints;
        if (_cp != null) _cp.OnTeamPointsChanged += Refresh;
        Refresh();
    }

    private void OnDisable()
    {
        if (_cp != null) _cp.OnTeamPointsChanged -= Refresh;
    }

    private void Refresh()
    {
        var state    = ServiceLocator.Game?.State;
        var settings = ServiceLocator.GameSettings;
        int maxPts   = settings != null ? settings.MaxTeamPoints : 300;
        if (maxPts <= 0) maxPts = 300;

        int pts0 = state?.TeamPoints != null && state.TeamPoints.Length > 0 ? state.TeamPoints[0] : 0;
        int pts1 = state?.TeamPoints != null && state.TeamPoints.Length > 1 ? state.TeamPoints[1] : 0;

        float w0 = Mathf.Min(0.5f, (float)pts0 / maxPts * 0.5f);
        float w1 = Mathf.Min(0.5f, (float)pts1 / maxPts * 0.5f);

        if (fill0 != null)
        {
            fill0.anchorMin = Vector2.zero;
            fill0.anchorMax = new Vector2(w0, 1f);
            fill0.offsetMin = Vector2.zero;
            fill0.offsetMax = Vector2.zero;
        }
        if (fill1 != null)
        {
            fill1.anchorMin = new Vector2(1f - w1, 0f);
            fill1.anchorMax = Vector2.one;
            fill1.offsetMin = Vector2.zero;
            fill1.offsetMax = Vector2.zero;
        }
        if (label0 != null) label0.text = $"DESERT SQUAD — {pts0} PTS";
        if (label1 != null) label1.text = $"{pts1} PTS — JUNGLE SQUAD";
    }
}
