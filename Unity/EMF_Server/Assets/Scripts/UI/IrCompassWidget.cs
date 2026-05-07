using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Compass widget for IR hit visualisation.
/// Outer ring: 8 cyan dots (one per sensor direction) — all that fired flash simultaneously.
/// Inner cross: 4 yellow squares (N/E/S/W) — the one matching the averaged cardinal flashes.
/// Call Flash(mask, cardinalDir) on each hit.
/// </summary>
public class IrCompassWidget : MonoBehaviour
{
    private static readonly string[] Dirs   = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    private static readonly float[]  Angles = { 90f, 45f, 0f, -45f, -90f, -135f, 180f, 135f };

    private const float Radius   = 13f;
    private const float DotSize  = 5f;
    private const float FlashDur = 0.35f;

    private static readonly Color C_DIM = new Color(0.25f, 0.25f, 0.25f);
    private static readonly Color C_LIT = new Color(0f, 1f, 0.8f);

    // Inner cardinal cross
    private static readonly string[] CardinalDirs   = { "N", "E", "S", "W" };
    private static readonly float[]  CardinalAngles = { 90f, 0f, -90f, 180f };

    private const float CardinalRadius = 6f;
    private const float CardinalSize   = 6f;

    private static readonly Color C_CARDINAL_DIM = new Color(0.15f, 0.15f, 0.15f);
    private static readonly Color C_CARDINAL_LIT = new Color(1f, 0.85f, 0f);

    private Image[] _dots;
    private Image[] _cardinals;

    private void Awake()
    {
        // Outer ring — 8 sensor dots
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
            img.color         = C_DIM;
            img.raycastTarget = false;
            _dots[i] = img;
        }

        // Inner cross — 4 averaged-direction squares
        _cardinals = new Image[4];
        for (int i = 0; i < 4; i++)
        {
            float rad = CardinalAngles[i] * Mathf.Deg2Rad;
            float x   = Mathf.Cos(rad) * CardinalRadius;
            float y   = Mathf.Sin(rad) * CardinalRadius;

            var go = new GameObject("Cardinal_" + CardinalDirs[i]);
            var rt = go.AddComponent<RectTransform>();
            go.transform.SetParent(transform, false);

            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta        = new Vector2(CardinalSize, CardinalSize);

            var img = go.AddComponent<Image>();
            img.color         = C_CARDINAL_DIM;
            img.raycastTarget = false;
            _cardinals[i] = img;
        }
    }

    /// <summary>
    /// Flash all sensor dots set in mask (cyan) and the averaged cardinal square (yellow).
    /// </summary>
    public void Flash(byte mask, string cardinalDir)
    {
        if (_dots == null || _cardinals == null) return;

        for (int i = 0; i < 8; i++)
            if ((mask & (1 << i)) != 0)
                StartCoroutine(FlashDotCoroutine(_dots[i], C_LIT, C_DIM));

        int cIdx = System.Array.IndexOf(CardinalDirs, cardinalDir);
        if (cIdx >= 0)
            StartCoroutine(FlashDotCoroutine(_cardinals[cIdx], C_CARDINAL_LIT, C_CARDINAL_DIM));
    }

    private IEnumerator FlashDotCoroutine(Image dot, Color litColor, Color dimColor)
    {
        dot.color = litColor;
        yield return new WaitForSeconds(FlashDur);
        if (dot.color == litColor)
            dot.color = dimColor;
    }
}
