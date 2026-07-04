// LobbyLayoutBuilder.cs
// Attach to Canvas/LobbyPanel. Repositions all lobby children to the
// three-column layout (Robots | Players | Settings) on recompile in Edit mode.
// Bump SENTINEL to force a one-time re-apply after layout changes.

using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

[ExecuteAlways]
public class LobbyLayoutBuilder : MonoBehaviour
{
    const string SENTINEL = "__llb_v2";

    void Awake()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) { ApplyIfNeeded(); return; }
#endif
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (Application.isPlaying) return;
        EditorApplication.delayCall += ApplyIfNeeded;
    }

    void ApplyIfNeeded()
    {
        if (this == null) return;
        if (transform.Find(SENTINEL) != null) return;

        ApplyLayout();

        var sentinel = new GameObject(SENTINEL);
        sentinel.transform.SetParent(transform, false);
        sentinel.hideFlags = HideFlags.HideInHierarchy;

        EditorSceneManager.MarkSceneDirty(gameObject.scene);
        var scene = gameObject.scene;
        EditorApplication.delayCall += () =>
        {
            if (gameObject != null) EditorSceneManager.SaveScene(scene);
        };
        Debug.Log("[LobbyLayoutBuilder] Layout applied and scene saved.");
    }
#endif

    void ApplyLayout()
    {
        EnsureBeaconsUI();

        const float cL = 0.33f;  // left | middle column boundary
        const float cR = 0.67f;  // middle | right column boundary

        void Stretch(string childName,
                     float xMin, float yMin, float xMax, float yMax,
                     float l = 0f, float b = 0f, float r = 0f, float t = 0f)
        {
            var child = transform.Find(childName);
            if (child == null) return;
            var rt = child.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin  = new Vector2(xMin, yMin);
            rt.anchorMax  = new Vector2(xMax, yMax);
            rt.pivot      = new Vector2(0.5f, 0.5f);
            rt.offsetMin  = new Vector2(l, b);
            rt.offsetMax  = new Vector2(-r, -t);
        }

        // Title — full width
        Stretch("TitleLabel", 0f, 0.93f, 1f, 1f, 12f, 2f, 12f, 2f);

        // ── LEFT — Robots ──────────────────────────────────────────────────────
        Stretch("RobotsLabel",           0f,         0.88f, cL * 0.72f, 0.93f, 10f, 1f, 2f, 1f);
        Stretch("NumRobotsText",         cL * 0.72f, 0.88f, cL,         0.93f, 0f,  1f, 6f, 1f);
        Stretch("RobotsScrollView",      0f,         0.28f, cL,         0.88f, 10f, 2f, 4f, 2f);
        Stretch("BeaconsLabel",          0f,         0.24f, cL,         0.28f, 10f, 1f, 4f, 1f);
        Stretch("BeaconsScrollView",     0f,         0.12f, cL,         0.24f, 10f, 2f, 4f, 2f);
        Stretch("AddFakeRobotButton",    0f,         0.06f, cL * 0.5f,  0.12f, 10f, 2f, 2f, 2f);
        Stretch("RemoveLastRobotButton", cL * 0.5f,  0.06f, cL,         0.12f, 2f,  2f, 4f, 2f);

        // ── MIDDLE — Players ───────────────────────────────────────────────────
        Stretch("PlayersLabel",      cL, 0.88f, cR, 0.93f, 4f, 1f, 4f, 1f);
        Stretch("PlayersScrollView", cL, 0.08f, cR, 0.88f, 4f, 2f, 4f, 2f);
        Stretch("AddPlayerButton",   cL, 0.01f, cR, 0.08f, 4f, 2f, 4f, 2f);

        // ── RIGHT — Game Settings + Start Game ─────────────────────────────────
        Stretch("GameSettingsPanel", cR,          0.14f, 1f,    0.93f, 4f, 4f, 8f, 2f);
        Stretch("StartGameButton",   cR + 0.01f,  0.01f, 0.99f, 0.13f, 4f, 4f, 4f, 4f);

        // Back button sits below Start Game if present
        Stretch("LobbyBackButton",   cR + 0.01f,  0.14f, 0.99f, 0.22f, 4f, 2f, 4f, 2f);
    }

    // Builds the Beacons list the first time this layout runs, by cloning the
    // already-wired RobotsScrollView (ScrollRect + Viewport + Content with
    // VerticalLayoutGroup/ContentSizeFitter) rather than hand-building a new
    // ScrollRect hierarchy. Also attaches BeaconsPanelPresenter and wires its
    // private 'content' field via reflection, matching PlayingPanelBuilder's
    // approach for runtime-built UI with no prefab/Inspector wiring available.
    void EnsureBeaconsUI()
    {
#if UNITY_EDITOR
        if (transform.Find("BeaconsScrollView") == null)
        {
            var robotsScrollView = transform.Find("RobotsScrollView");
            if (robotsScrollView != null)
            {
                var clone = Instantiate(robotsScrollView.gameObject, transform);
                clone.name = "BeaconsScrollView";

                var content = clone.transform.Find("Viewport/Content");
                if (content != null)
                    for (int i = content.childCount - 1; i >= 0; i--)
                        DestroyImmediate(content.GetChild(i).gameObject);

                var presenter = GetComponent<BeaconsPanelPresenter>();
                if (presenter == null) presenter = gameObject.AddComponent<BeaconsPanelPresenter>();

                var field = typeof(BeaconsPanelPresenter).GetField("content",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) field.SetValue(presenter, content);
                else Debug.LogWarning("[LobbyLayoutBuilder] BeaconsPanelPresenter.content field not found");
            }
        }

        if (transform.Find("BeaconsLabel") == null)
        {
            var robotsLabel = transform.Find("RobotsLabel");
            if (robotsLabel != null)
            {
                var labelClone = Instantiate(robotsLabel.gameObject, transform);
                labelClone.name = "BeaconsLabel";
                var tmp = labelClone.GetComponent<TextMeshProUGUI>();
                if (tmp != null) tmp.text = "BEACONS";
            }
        }
#endif
    }
}
