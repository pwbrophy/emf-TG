// ESP32VideoReceiver.cs - receives JPEG bytes per robot and shows the active robot's stream
using UnityEngine;
using UnityEngine.UI;

public sealed class ESP32VideoReceiver : MonoBehaviour
{
    public static ESP32VideoReceiver Instance;  // Singleton instance any code can call

    [Header("UI target (assign a RawImage in the Inspector)")]
    [SerializeField] private RawImage target;   // Where the video appears

    private Texture2D _tex;                     // Reusable texture for decoded JPEGs
    private string _activeRobotId;              // Robot whose frames we accept/render
    private int _frameCount;                    // How many frames we have rendered
    private int _lastLogged;                    // Last count we logged (for throttling)

    private void Awake()
    {
        Debug.Log("Video Receiver Awake");
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
        if (target != null) target.texture = _tex;
    }

    public void SetActiveRobot(string robotId)
    {
        _activeRobotId = robotId;
        _frameCount = 0;
        _lastLogged = -1;
        Debug.Log($"[VideoRX] Active robot set to {robotId}");

        if (target != null && _tex != null)
        {
            _tex.Reinitialize(2, 2);
            _tex.Apply(false, false);
            target.texture = _tex;
        }
    }

    public void ClearActiveRobot()
    {
        _activeRobotId = null;
        if (target != null && _tex != null)
        {
            _tex.Reinitialize(2, 2);
            _tex.Apply(false, false);
            target.texture = _tex;
        }
    }

    public void SetTarget(RawImage ri)
    {
        target = ri;
        if (target != null && _tex != null)
            target.texture = _tex;
    }

    public void ReceiveFrame(string robotId, byte[] jpegBytes)
    {
        if (string.IsNullOrEmpty(_activeRobotId)) return;
        if (robotId != _activeRobotId) return;
        if (jpegBytes == null || jpegBytes.Length == 0) return;
        if (_tex == null) return;

        bool decoded = _tex.LoadImage(jpegBytes, markNonReadable: false);
        if (!decoded)
        {
            Debug.LogWarning($"[VideoRX] JPEG decode failed (len={jpegBytes.Length})");
            return;
        }

        _tex.Apply(false, false);

        if (target != null && target.texture != _tex)
            target.texture = _tex;

        _frameCount++;

        if (_frameCount / 15 != _lastLogged / 15)
        {
            _lastLogged = _frameCount;
        }
    }
}
