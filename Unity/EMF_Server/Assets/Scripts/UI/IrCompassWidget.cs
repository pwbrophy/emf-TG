using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 8-dot compass rose that briefly flashes the direction of an incoming IR hit.
/// Add this component to a 36×36 px RectTransform; it builds its own child dots in Awake.
/// Call Flash(direction) where direction is one of "N","NE","E","SE","S","SW","W","NW".
/// </summary>
public class IrCompassWidget : MonoBehaviour
{
    private static readonly string[] Dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    // Angle in degrees from East, counter-clockwise (Unity UI: +Y = up)
    private static readonly float[] Angles = { 90f, 45f, 0f, -45f, -90f, -135f, 180f, 135f };

    private const float Radius   = 13f;
    private const float DotSize  = 5f;
    private const float FlashDur = 0.35f;

    private static readonly Color C_DIM = new Color(0.25f, 0.25f, 0.25f);
    private static readonly Color C_LIT = new Color(0f, 1f, 0.8f);

    private Image[] _dots;

    private void Awake()
    {
        _dots = new Image[8];
        for (int i = 0; i < 8; i++)
        {
            float rad = Angles[i] * Mathf.Deg2Rad;
            float x   = Mathf.Cos(rad) * Radius;
            float y   = Mathf.Sin(rad) * Radius;

            var go = new GameObject(Dirs[i]);
            var rt = go.AddComponent<RectTransform>();
            go.transform.SetParent(transform, false);

            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta        = new Vector2(DotSize, DotSize);

            var img = go.AddComponent<Image>();
            img.color          = C_DIM;
            img.raycastTarget  = false;
            _dots[i] = img;
        }
    }

    public void Flash(string direction)
    {
        int idx = System.Array.IndexOf(Dirs, direction);
        if (idx < 0 || _dots == null) return;
        StartCoroutine(FlashCoroutine(idx));
    }

    private IEnumerator FlashCoroutine(int idx)
    {
        _dots[idx].color = C_LIT;
        yield return new WaitForSeconds(FlashDur);
        // Only restore if still lit — prevents a second flash from being cancelled
        if (_dots[idx].color == C_LIT)
            _dots[idx].color = C_DIM;
    }
}
