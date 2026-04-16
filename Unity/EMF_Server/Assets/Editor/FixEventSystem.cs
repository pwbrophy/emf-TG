using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Replaces StandaloneInputModule on the EventSystem with InputSystemUIInputModule
/// so the project works with the new Input System package.
/// </summary>
public class FixEventSystem
{
    public static void Execute()
    {
        var es = Object.FindFirstObjectByType<EventSystem>();
        if (es == null)
        {
            Debug.LogError("[FixEventSystem] No EventSystem found in scene.");
            return;
        }

        var standalone = es.GetComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        if (standalone != null)
        {
            Object.DestroyImmediate(standalone);
            Debug.Log("[FixEventSystem] Removed StandaloneInputModule.");
        }

        // InputSystemUIInputModule lives in UnityEngine.InputSystem.UI
        var existingNew = es.GetComponent("InputSystemUIInputModule");
        if (existingNew == null)
        {
            // Add via type name to avoid hard assembly reference
            var type = System.Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");

            if (type == null)
            {
                // Try alternative assembly name
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
                    if (type != null) break;
                }
            }

            if (type != null)
            {
                es.gameObject.AddComponent(type);
                Debug.Log("[FixEventSystem] Added InputSystemUIInputModule.");
            }
            else
            {
                Debug.LogError("[FixEventSystem] Could not find InputSystemUIInputModule type. Is com.unity.inputsystem installed?");
            }
        }
        else
        {
            Debug.Log("[FixEventSystem] InputSystemUIInputModule already present.");
        }

        EditorUtility.SetDirty(es.gameObject);
        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log("[FixEventSystem] Done.");
    }
}
