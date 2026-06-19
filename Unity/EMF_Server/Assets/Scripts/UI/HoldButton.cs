using System;
using UnityEngine;
using UnityEngine.EventSystems;

// Fires OnPressed on pointer-down and OnReleased on pointer-up or pointer-exit.
// Used for lobby turret-centre buttons so the turret stops when the operator lifts their finger.
public class HoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public Action OnPressed;
    public Action OnReleased;

    bool _held;

    public void OnPointerDown(PointerEventData _) { _held = true;  OnPressed?.Invoke(); }
    public void OnPointerUp(PointerEventData _)   { Release(); }
    public void OnPointerExit(PointerEventData _) { Release(); }

    void Release() { if (!_held) return; _held = false; OnReleased?.Invoke(); }
}
