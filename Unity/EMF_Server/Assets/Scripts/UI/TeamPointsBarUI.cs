using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tug-of-war team points bar. Blue fills from the left, red from the right.
/// Subscribes to CapturePointService.OnTeamPointsChanged.
/// </summary>
public class TeamPointsBarUI : MonoBehaviour
{
    [SerializeField] public RectTransform fill0;   // blue (Alliance 1, left)
    [SerializeField] public RectTransform fill1;   // red  (Alliance 2, right)
    [SerializeField] public TextMeshProUGUI label0;
    [SerializeField] public TextMeshProUGUI label1;

    private CapturePointService _cp;

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

        float w0 = Mathf.Min(0.5f, (float)pts0 / maxPts);
        float w1 = Mathf.Min(0.5f, (float)pts1 / maxPts);

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
        if (label0 != null) label0.text = $"ALLIANCE 1 — {pts0} PTS";
        if (label1 != null) label1.text = $"{pts1} PTS — ALLIANCE 2";
    }
}
