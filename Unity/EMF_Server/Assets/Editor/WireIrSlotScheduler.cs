using UnityEngine;
using UnityEditor;

public class WireIrSlotScheduler
{
    public static void Execute()
    {
        var servers = GameObject.Find("Servers");
        if (servers == null)
        {
            Debug.LogError("[Wire] 'Servers' GameObject not found in scene.");
            return;
        }

        var existing = servers.GetComponent<IrSlotScheduler>();
        if (existing != null)
        {
            Debug.Log("[Wire] IrSlotScheduler already present on Servers.");
            return;
        }

        servers.AddComponent<IrSlotScheduler>();
        Debug.Log("[Wire] IrSlotScheduler added to Servers.");

        EditorUtility.SetDirty(servers);
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
        Debug.Log("[Wire] Scene saved.");
    }
}
