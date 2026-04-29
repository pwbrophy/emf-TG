using UnityEngine;
using UnityEngine.UI;

public class CapturePointsPanelUI : MonoBehaviour
{
    [SerializeField] public Image circle0;  // North
    [SerializeField] public Image circle1;  // Centre
    [SerializeField] public Image circle2;  // South

    static readonly Color C_BLUE    = new Color(0.29f, 0.62f, 1.00f);
    static readonly Color C_RED     = new Color(1.00f, 0.42f, 0.21f);
    static readonly Color C_NEUTRAL = new Color(0.20f, 0.20f, 0.20f);

    private CapturePointService _cp;

    private void OnEnable()
    {
        _cp = ServiceLocator.CapturePoints;
        if (_cp != null) _cp.OnPointCaptured += HandlePointCaptured;
        Refresh();
    }

    private void OnDisable()
    {
        if (_cp != null) _cp.OnPointCaptured -= HandlePointCaptured;
    }

    private void Refresh()
    {
        var state  = ServiceLocator.Game?.State;
        var owners = state?.CapturePointOwners ?? new int[] { -1, -1, -1 };
        SetCircle(circle0, owners.Length > 0 ? owners[0] : -1);
        SetCircle(circle1, owners.Length > 1 ? owners[1] : -1);
        SetCircle(circle2, owners.Length > 2 ? owners[2] : -1);
    }

    private void SetCircle(Image img, int owner)
    {
        if (img == null) return;
        img.color = owner == 0 ? C_BLUE : owner == 1 ? C_RED : C_NEUTRAL;
    }

    private void HandlePointCaptured(int pointIndex, int allianceIndex, string pointName)
    {
        switch (pointIndex)
        {
            case 0: SetCircle(circle0, allianceIndex); break;
            case 1: SetCircle(circle1, allianceIndex); break;
            case 2: SetCircle(circle2, allianceIndex); break;
        }
    }
}
