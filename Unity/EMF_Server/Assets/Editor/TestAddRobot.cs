using UnityEngine;
using UnityEngine.UI;

public static class TestAddRobot
{
    public static void Execute()
    {
        // Find and click the AddFakeRobotButton
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("[TestAddRobot] Canvas not found"); return; }

        var btn = canvas.transform.Find("LobbyPanel/AddFakeRobotButton");
        if (btn == null) { Debug.LogError("[TestAddRobot] AddFakeRobotButton not found"); return; }

        var button = btn.GetComponent<Button>();
        if (button != null)
            button.onClick.Invoke();
        else
            Debug.LogError("[TestAddRobot] No Button component");
    }
}
