using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// Attach this to the SAME GameObject that has your Slider component.
// It will snap the slider back to 0.5 when you release the mouse/finger.
public class SliderCenterOnRelease : MonoBehaviour, IPointerUpHandler, IEndDragHandler
{
    [SerializeField] private Slider slider;

    private void Awake()
    {
        if (slider == null) slider = GetComponent<Slider>();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (slider != null) slider.value = 0.5f;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (slider != null) slider.value = 0.5f;
    }
}
