using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public class CreateSlider
{
    public static void Execute()
    {
        // Find PlayingPanel
        var playingPanel = GameObject.Find("Canvas/PlayingPanel");
        if (playingPanel == null)
        {
            Debug.LogError("[CreateSlider] Could not find Canvas/PlayingPanel");
            return;
        }

        // Create slider via Unity's default menu method
        var sliderGO = DefaultControls.CreateSlider(new DefaultControls.Resources());
        sliderGO.name = "TurretSlider";
        sliderGO.transform.SetParent(playingPanel.transform, false);

        // Set to horizontal, range -1..1, default 0.5 (maps to 0 speed)
        var slider = sliderGO.GetComponent<Slider>();
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = 0.5f;
        slider.wholeNumbers = false;

        // Also add SliderCenterOnRelease
        sliderGO.AddComponent<SliderCenterOnRelease>();

        EditorUtility.SetDirty(playingPanel);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[CreateSlider] TurretSlider created under PlayingPanel");
    }
}
